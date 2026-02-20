using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace EffinitiveFramework.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RoutingBenchmarks
{
    private EffinitiveFramework.Core.Router? _router;
    private const string ExactMethod  = "GET";
    private const string ExactPath    = "/api/users";
    private const string ParamPath    = "/api/users/42";
    private const string ParamPattern = "/api/users/{id}";

    [GlobalSetup]
    public void Setup()
    {
        _router = new EffinitiveFramework.Core.Router();

        // Register exact-match and parameterised routes
        _router.AddRoute("GET",    "/api/users",     (int id)   => Task.FromResult($"User {id}"));
        _router.AddRoute("POST",   "/api/users",     (string n) => Task.FromResult($"Created {n}"));
        _router.AddRoute("GET",    ParamPattern,     (int id)   => Task.FromResult($"User {id}"));
        _router.AddRoute("GET",    "/api/products",  (int id)   => Task.FromResult($"Product {id}"));
        _router.AddRoute("DELETE", "/api/products",  (int id)   => Task.FromResult($"Deleted {id}"));

        // Required after all AddRoute calls — materialises FrozenDictionary
        _router.Freeze();
    }

    /// <summary>Exact-match via FrozenDictionary AlternateLookup — zero string allocation.</summary>
    [Benchmark(Baseline = true, Description = "Exact match (FrozenDictionary, no alloc)")]
    public EffinitiveFramework.Core.RouteMatch? ExactMatch()
        => _router!.FindRoute(ExactMethod.AsSpan(), ExactPath.AsSpan());

    /// <summary>Parameterised match via pre-split span scan — no Split('/') allocation.</summary>
    [Benchmark(Description = "Param match (span scan, pre-split segments)")]
    public EffinitiveFramework.Core.RouteMatch? ParamMatch()
        => _router!.FindRoute(ExactMethod.AsSpan(), ParamPath.AsSpan());
}

[MemoryDiagnoser]
public class EndpointBenchmarks
{
    private TestSyncEndpoint? _syncEndpoint;
    private TestAsyncEndpoint? _asyncEndpoint;

    [GlobalSetup]
    public void Setup()
    {
        _syncEndpoint = new TestSyncEndpoint();
        _asyncEndpoint = new TestAsyncEndpoint();
    }

    [Benchmark]
    public async Task<TestResponse> HandleSyncEndpoint()
    {
        return await _syncEndpoint!.HandleAsync(new TestRequest { Id = 1, Name = "Test" });
    }

    [Benchmark]
    public async Task<TestResponse> HandleAsyncEndpoint()
    {
        return await _asyncEndpoint!.HandleAsync(new TestRequest { Id = 1, Name = "Test" });
    }

    // ValueTask-based endpoint for synchronous operations
    public class TestSyncEndpoint : EffinitiveFramework.Core.EndpointBase<TestRequest, TestResponse>
    {
        protected override string Method => "GET";
        protected override string Route => "/test";

        public override ValueTask<TestResponse> HandleAsync(TestRequest request, CancellationToken cancellationToken = default)
        {
            // Synchronous operation - ValueTask is optimal here
            return ValueTask.FromResult(new TestResponse 
            { 
                Message = $"Hello {request.Name}",
                ProcessedId = request.Id 
            });
        }
    }

    // Task-based endpoint for async I/O operations
    public class TestAsyncEndpoint : EffinitiveFramework.Core.AsyncEndpointBase<TestRequest, TestResponse>
    {
        protected override string Method => "GET";
        protected override string Route => "/test-async";

        public override async Task<TestResponse> HandleAsync(TestRequest request, CancellationToken cancellationToken = default)
        {            
            return await Task.FromResult(new TestResponse 
            { 
                Message = $"Hello {request.Name}",
                ProcessedId = request.Id 
            });
        }
    }

    public record TestRequest
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    public record TestResponse
    {
        public string Message { get; init; } = string.Empty;
        public int ProcessedId { get; init; }
    }
}

/// <summary>
/// Isolates the reflection-vs-compiled-delegate hot path in EndpointInvoker.
/// Run with: dotnet run -c Release -- --filter *EndpointInvocation*
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class EndpointInvocationBenchmarks
{
    // Reflection path — MethodInfo.Invoke (simulates current/old behavior)
    private MethodInfo _handleMethod = null!;
    private EndpointBenchmarks.TestSyncEndpoint _syncEndpoint = null!;
    private EndpointBenchmarks.TestRequest _request = null!;

    // Compiled-delegate path — EndpointInvoker (new behavior)
    private Func<object, object?, CancellationToken, Task<object?>> _compiled = null!;

    [GlobalSetup]
    public void Setup()
    {
        _syncEndpoint = new EndpointBenchmarks.TestSyncEndpoint();
        _request = new EndpointBenchmarks.TestRequest { Id = 1, Name = "BenchmarkTest" };

        // Pre-look up the MethodInfo once (matches what the old server did per-request)
        _handleMethod = typeof(EndpointBenchmarks.TestSyncEndpoint)
            .GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance)!;

        // Build compiled delegate the same way EndpointInvoker.Build() does it
        var invoker = EffinitiveFramework.Core.EndpointInvoker.Build(typeof(EndpointBenchmarks.TestSyncEndpoint));
        _compiled = invoker.InvokeAsync;
    }

    /// <summary>Baseline: MethodInfo.Invoke — boxes args, boxes return, no JIT inlining.</summary>
    [Benchmark(Baseline = true, Description = "MethodInfo.Invoke (old path)")]
    public async Task<object?> ReflectionInvoke()
    {
        // Mirrors the old ExecuteEndpointAsync: Invoke → box ValueTask<T> → AsTask → Result
        var result = _handleMethod.Invoke(_syncEndpoint, new object[] { _request, CancellationToken.None });
        var resultType = result!.GetType();
        var asTaskMethod = resultType.GetMethod("AsTask")!;
        var vTask = (Task)asTaskMethod.Invoke(result, null)!;
        await vTask;
        var resultProp = vTask.GetType().GetProperty("Result")!;
        return resultProp.GetValue(vTask);
    }

    /// <summary>New path: compiled Expression.Lambda delegate — direct call, no boxing.</summary>
    [Benchmark(Description = "Compiled delegate (new path)")]
    public Task<object?> CompiledInvoke()
        => _compiled(_syncEndpoint, _request, CancellationToken.None);
}

public class Program
{
    public static void Main(string[] args)
    {
        // Use BenchmarkSwitcher so you can run any benchmark class from the CLI:
        //   dotnet run -c Release                         → picks Http2Benchmarks (default)
        //   dotnet run -c Release -- --filter *Invocation*  → runs EndpointInvocationBenchmarks
        //   dotnet run -c Release -- --filter *Routing*     → runs RoutingBenchmarks
        if (args.Length == 0)
        {
            // Default: run the HTTP/2 end-to-end benchmarks
            BenchmarkRunner.Run<Http2Benchmarks>();
        }
        else
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
