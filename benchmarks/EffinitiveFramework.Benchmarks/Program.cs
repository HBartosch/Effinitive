using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace EffinitiveFramework.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class RoutingBenchmarks
{
    private EffinitiveFramework.Core.Router? _router;
    private const string TestRoute = "/api/users";
    private const string TestMethod = "GET";

    [GlobalSetup]
    public void Setup()
    {
        _router = new EffinitiveFramework.Core.Router();
        
        // Register sample routes
        _router.AddRoute("GET", "/api/users", (int id) => Task.FromResult($"User {id}"));
        _router.AddRoute("POST", "/api/users", (string name) => Task.FromResult($"Created {name}"));
        _router.AddRoute("GET", "/api/products", (int id) => Task.FromResult($"Product {id}"));
        _router.AddRoute("DELETE", "/api/products", (int id) => Task.FromResult($"Deleted {id}"));
    }

    [Benchmark]
    public void RouteMatching()
    {
        _router!.FindRoute(TestMethod.AsSpan(), TestRoute.AsSpan());
    }

    [Benchmark]
    public void RouteMatchingWithAllocation()
    {
        var method = "GET";
        var route = "/api/users";
        _router!.FindRoute(method.AsSpan(), route.AsSpan());
    }
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

public class Program
{
    public static void Main(string[] args)
    {
        // Run HTTP/2 benchmarks directly
        BenchmarkRunner.Run<FrameworkComparisonBenchmarks>();
    }
}
