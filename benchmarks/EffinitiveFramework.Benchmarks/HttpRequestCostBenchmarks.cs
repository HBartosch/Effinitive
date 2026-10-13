using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EffinitiveFramework.Benchmarks;

/// <summary>
/// What one HTTP/1.1 request costs between arriving as bytes and leaving as a response,
/// with the socket taken out.
/// </summary>
/// <remarks>
/// The stages run in the order the connection loop runs them. The response object is reused
/// across iterations because the connection loop reuses one per connection and resets it per
/// request; the request is allocated per iteration because the connection allocates one per
/// request.
///
/// Two routes are measured. The endpoint route is registered the way MapEndpoints registers
/// one, with a compiled invoker, which is what reaches the direct fast path. The delegate
/// route is registered through the public AddRoute(string, string, Delegate) overload, which
/// leaves EndpointType null and so cannot reach that path.
///
/// Run: dotnet run -c Release -f net10.0 -- --filter *HttpRequestCost*
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class HttpRequestCostBenchmarks
{
    public sealed class PlainEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/plain";
        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult("Hello, World!");
    }

    private EffinitiveServer _server = null!;
    private HttpResponse _response = null!;
    private byte[] _rawEndpoint = null!;
    private byte[] _rawDelegate = null!;
    private HttpRequest _parsed = null!;
    private HttpRequest _reused = null!;

    private const int MaxBodySize = 30 * 1024 * 1024;

    [GlobalSetup]
    public void Setup()
    {
        var router = new Router();
        router.AddEndpointType("GET", "/plain", typeof(PlainEndpoint), EndpointInvoker.Build(typeof(PlainEndpoint)));
        router.AddRoute("GET", "/delegate",
            (EmptyRequest _, CancellationToken _) => Task.FromResult("Hello, World!"));
        router.Freeze();

        var services = new ServiceCollection();
        services.AddTransient(typeof(PlainEndpoint), typeof(PlainEndpoint));

        _server = new EffinitiveServer(
            new ServerOptions { HttpPort = 0, EnableDebugLogging = false },
            router,
            services.BuildServiceProvider());

        _rawEndpoint = Raw("/plain");
        _rawDelegate = Raw("/delegate");

        _response = new HttpResponse();
        _parsed = ParseOnce(_rawEndpoint);
        _reused = new HttpRequest();
    }

    private static byte[] Raw(string path) => Encoding.ASCII.GetBytes(
        $"GET {path} HTTP/1.1\r\nHost: localhost:8080\r\nUser-Agent: benchmark\r\nAccept: */*\r\n\r\n");

    private static HttpRequest ParseOnce(byte[] raw)
    {
        var buffer = new ReadOnlySequence<byte>(raw);
        var request = new HttpRequest();
        HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _, MaxBodySize);
        return request;
    }

    /// <summary>Bytes to a populated request, which is what the read loop does per request.</summary>
    [Benchmark]
    public HttpRequest Parse() => ParseOnce(_rawEndpoint);

    /// <summary>The request object alone, which the connection allocates fresh per request.</summary>
    [Benchmark]
    public HttpRequest NewRequest() => new();

    /// <summary>Parsing alone, into a request that is reset rather than allocated.</summary>
    [Benchmark]
    public bool ParseIntoReused()
    {
        _reused.Reset();
        var buffer = new ReadOnlySequence<byte>(_rawEndpoint);
        return HttpRequestParser.TryParseRequest(ref buffer, _reused, out _, out _, MaxBodySize);
    }

    /// <summary>The pre-routing checks the connection loop runs before touching the router.</summary>
    [Benchmark]
    public object Validate() => _server.ValidateRequest(_parsed);

    /// <summary>The entity-tag and conditional-request handling applied to the result.</summary>
    [Benchmark]
    public void Conditional() => _server.ApplyConditionalHeaders(_parsed, _response, isHead: false);

    /// <summary>Routing, invoking the endpoint, and serializing whatever it returned.</summary>
    [Benchmark]
    public async Task HandleEndpoint()
    {
        _response.Reset();
        await _server.HandleRequestAsync(_parsed, _response, CancellationToken.None);
    }

    /// <summary>Everything above, in the order the connection loop runs it.</summary>
    [Benchmark(Baseline = true)]
    public async Task WholeEndpoint()
    {
        var request = ParseOnce(_rawEndpoint);
        _server.ValidateRequest(request);
        _response.Reset();
        await _server.HandleRequestAsync(request, _response, CancellationToken.None);
        _server.ApplyConditionalHeaders(request, _response, isHead: false);
    }

    /// <summary>The same, for a route the fast path cannot serve.</summary>
    [Benchmark]
    public async Task WholeDelegate()
    {
        var request = ParseOnce(_rawDelegate);
        _server.ValidateRequest(request);
        _response.Reset();
        await _server.HandleRequestAsync(request, _response, CancellationToken.None);
        _server.ApplyConditionalHeaders(request, _response, isHead: false);
    }

    /// <summary>
    /// The whole path again, with the request reset and reused the way the response already is.
    /// </summary>
    /// <remarks>
    /// The connection loop does not do this today: it reuses one response per connection but
    /// allocates a request per request. This measures what wiring the existing Reset() into that
    /// loop would be worth, before deciding whether the lifetime work it needs is justified.
    /// </remarks>
    [Benchmark]
    public async Task WholeEndpointReusedRequest()
    {
        _reused.Reset();
        var buffer = new ReadOnlySequence<byte>(_rawEndpoint);
        HttpRequestParser.TryParseRequest(ref buffer, _reused, out _, out _, MaxBodySize);

        _server.ValidateRequest(_reused);
        _response.Reset();
        await _server.HandleRequestAsync(_reused, _response, CancellationToken.None);
        _server.ApplyConditionalHeaders(_reused, _response, isHead: false);
    }
}
