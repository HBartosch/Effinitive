using System.Net;
using BenchmarkDotNet.Attributes;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;
using EffinitiveFramework.Core.Http3;

namespace EffinitiveFramework.Benchmarks;

/// <summary>
/// What one HTTP/3 request costs in the parts that are ours, with QUIC taken out.
/// </summary>
/// <remarks>
/// Measured on the host, the HTTP/3 path allocates about 4,900 bytes per request against about
/// 1,030 for HTTP/1.1. QUIC itself is not in that figure's control, but these three stages are:
/// turning the decoded field list into a request, building the response field list, and encoding
/// it with QPACK. This splits them so the 4,900 can be attributed rather than guessed at.
///
/// Run: dotnet run -c Release -f net10.0 -- --filter *Http3RequestCost*
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class Http3RequestCostBenchmarks
{
    private List<(string name, string value)> _requestHeaders = null!;
    private byte[] _body = null!;
    private IPAddress _peer = null!;
    private HttpResponse _response = null!;
    private QpackEncoder _encoder = null!;
    private List<(string name, string value)> _responseHeaders = null!;

    [GlobalSetup]
    public void Setup()
    {
        // The field list h2load sends for the arena's baseline-h3 target.
        _requestHeaders = new List<(string, string)>
        {
            (":method", "GET"),
            (":path", "/baseline2?a=1&b=1"),
            (":scheme", "https"),
            (":authority", "localhost:8443"),
            ("user-agent", "h2load nghttp2/1.64.0"),
        };

        _body = Array.Empty<byte>();
        _peer = IPAddress.Parse("127.0.0.1");
        _encoder = new QpackEncoder();

        _response = new HttpResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            Body = "2"u8.ToArray()
        };
        _response.Headers["ETag"] = "\"e3b0c44298fc1c14\"";
        _response.Headers["Last-Modified"] = "Wed, 01 Oct 2026 07:18:44 GMT";

        _responseHeaders = BuildResponseHeaders();
    }

    /// <summary>The decoded field list becoming an HttpRequest, once per request.</summary>
    [Benchmark]
    public HttpRequest ConvertRequest()
        => Http2RequestConverter.ConvertToHttp1Request(_requestHeaders, _body, _peer);

    /// <summary>
    /// Assembling the response field list, exactly as SendResponseAsync does it.
    /// </summary>
    /// <remarks>
    /// QPACK requires lowercase field names (RFC 9114 §4.1.2) and the response dictionary holds
    /// them in canonical case, so every header is lowercased on the way out, which allocates a
    /// string per header per response.
    /// </remarks>
    [Benchmark]
    public List<(string name, string value)> BuildResponseHeaders()
    {
        var bodyLength = _response.Body?.Length ?? 0;

        var headerList = new List<(string name, string value)>(6)
        {
            (":status", _response.StatusCode.ToString())
        };
        if (!string.IsNullOrEmpty(_response.ContentType))
            headerList.Add(("content-type", _response.ContentType));
        if (bodyLength > 0)
            headerList.Add(("content-length", bodyLength.ToString()));
        if (_response.Headers != null)
            foreach (var h in _response.Headers)
                headerList.Add((h.Key.ToLowerInvariant(), h.Value));

        return headerList;
    }

    /// <summary>QPACK encoding of that field list.</summary>
    [Benchmark]
    public byte[] EncodeHeaders() => _encoder.Encode(_responseHeaders);

    /// <summary>A fresh response object, which the HTTP/3 handler allocates per request.</summary>
    [Benchmark]
    public HttpResponse NewResponse() => new();

    /// <summary>Everything above, which is one request's worth of our own work.</summary>
    [Benchmark(Baseline = true)]
    public byte[] Whole()
    {
        var request = Http2RequestConverter.ConvertToHttp1Request(_requestHeaders, _body, _peer);
        var response = new HttpResponse { StatusCode = 200, ContentType = "application/json" };
        _ = request.Path;
        var list = BuildResponseHeaders();
        return _encoder.Encode(list);
    }
}
