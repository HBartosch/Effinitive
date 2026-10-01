using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// HTTP/2 and HTTP/3 both build their requests through the same conversion, from a decoded field
/// list rather than a request line.
/// </summary>
/// <remarks>
/// Neither protocol carries a version in the request: which one is in use is settled by ALPN or by
/// prior knowledge before anything is sent (RFC 9113 §3.3, RFC 9114 §3.1). The conversion therefore
/// states the version itself, and stated the same one for both, so an application inspecting an
/// HTTP/3 request was told it was HTTP/2.
/// </remarks>
public class RequestConversionTests
{
    private static readonly List<(string name, string value)> Fields =
    [
        (":method", "GET"),
        (":path", "/orders?page=2"),
        (":scheme", "https"),
        (":authority", "example.com"),
        ("user-agent", "probe"),
    ];

    [Fact]
    public void AnHttp2RequestReportsHttp2()
    {
        var request = Http2RequestConverter.ConvertToHttp1Request(Fields, [], null);

        Assert.Equal(HttpVersions.Http20, request.HttpVersion);
    }

    [Fact]
    public void AnHttp3RequestReportsHttp3()
    {
        var request = Http2RequestConverter.ConvertToHttp1Request(
            Fields, [], null, HttpVersions.Http30);

        Assert.Equal(HttpVersions.Http30, request.HttpVersion);
    }

    [Fact]
    public void PseudoHeadersBecomeTheRequestRatherThanHeaders()
    {
        var request = Http2RequestConverter.ConvertToHttp1Request(Fields, [], null);

        Assert.Equal("GET", request.Method);
        Assert.Equal("/orders?page=2", request.Path);
        Assert.True(request.IsHttps);

        // :authority carries what Host carries on HTTP/1.1 (RFC 9113 §8.3.1).
        Assert.Equal("example.com", request.Headers["host"]);
        Assert.Equal("probe", request.Headers["user-agent"]);

        Assert.DoesNotContain(request.Headers.Keys, k => k.StartsWith(':'));
    }

    [Fact]
    public void AnInsecureSchemeIsNotReportedAsSecure()
    {
        List<(string name, string value)> plaintext =
        [
            (":method", "GET"), (":path", "/"), (":scheme", "http"), (":authority", "example.com"),
        ];

        Assert.False(Http2RequestConverter.ConvertToHttp1Request(plaintext, [], null).IsHttps);
    }
}
