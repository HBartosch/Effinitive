using System.Buffers;
using System.Text;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for HttpRequestParser security validations.
/// All validation methods are private, tested through the public TryParseRequest API.
/// </summary>
public class HttpRequestParserSecurityTests
{
    // ── Valid requests ──

    [Fact]
    public void Parse_ValidGetRequest_Succeeds()
    {
        var request = Parse("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        Assert.Equal("GET", request.Method);
        Assert.Equal("/", request.Path);
        Assert.Equal("HTTP/1.1", request.HttpVersion);
    }

    [Fact]
    public void Parse_ValidPostWithBody_Succeeds()
    {
        var body = "{\"name\":\"test\"}";
        var raw = $"POST /api HTTP/1.1\r\nHost: localhost\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        var request = Parse(raw);
        Assert.Equal("POST", request.Method);
        Assert.Equal(body.Length, request.ContentLength);
        Assert.Equal(body, Encoding.UTF8.GetString(request.Body.Span));
    }

    // ── Method validation ──

    [Fact]
    public void Parse_LowercaseMethod_Throws501()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("get / HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(501, ex.StatusCode);
    }

    [Fact]
    public void Parse_MixedCaseMethod_Throws501()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("Get / HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(501, ex.StatusCode);
    }

    // ── URI validation ──

    [Fact]
    public void Parse_BackslashInUrl_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET /path\\file HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_PercentEncodedNull_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET /path%00file HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_PercentEncodedCR_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET /path%0dfile HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_PercentEncodedLF_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET /path%0afile HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    // ── HTTP version validation ──

    [Fact]
    public void Parse_Http20_Throws505()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET / HTTP/2.0\r\nHost: localhost\r\n\r\n"));
        Assert.Equal(505, ex.StatusCode);
    }

    // ── Header validation ──

    [Fact]
    public void Parse_UnderscoreInHeaderName_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET / HTTP/1.1\r\nHost: localhost\r\nX_Custom: value\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_DuplicateContentType_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET / HTTP/1.1\r\nHost: localhost\r\nContent-Type: text/plain\r\nContent-Type: text/html\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_DuplicateHost_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET / HTTP/1.1\r\nHost: a.com\r\nHost: b.com\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_MissingHost_Http11_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET / HTTP/1.1\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Parse_ContentLengthWithLeadingSpace_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() => Parse("GET / HTTP/1.1\r\nHost: localhost\r\nContent-Length: \t5\r\n\r\nhello"));
        Assert.Equal(400, ex.StatusCode);
    }

    // ── Transfer-Encoding + Content-Length conflict ──

    [Fact]
    public void Parse_TEAndCL_Throws400()
    {
        var ex = Assert.Throws<HttpParseException>(() =>
            Parse("POST / HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n"));
        Assert.Equal(400, ex.StatusCode);
    }

    // ── Incomplete request handling ──

    [Fact]
    public void Parse_IncompleteRequestLine_ReturnsFalse()
    {
        var bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n");
        var buffer = new ReadOnlySequence<byte>(bytes);
        var request = new HttpRequest();
        bool result = HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void Parse_EmptyBuffer_ReturnsFalse()
    {
        var buffer = new ReadOnlySequence<byte>(Array.Empty<byte>());
        var request = new HttpRequest();
        bool result = HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _);
        Assert.False(result);
    }

    // ── Absolute-form URI ──

    [Fact]
    public void Parse_AbsoluteFormUri_ExtractsHost()
    {
        var request = Parse("GET http://example.com/path HTTP/1.1\r\nHost: example.com\r\n\r\n");
        Assert.Equal("/path", request.Path);
        Assert.NotNull(request.Items);
        Assert.True(request.Items!.ContainsKey("AbsoluteFormHost"));
        Assert.Equal("example.com", request.Items["AbsoluteFormHost"]);
    }

    // ── Helper ──

    private static HttpRequest Parse(string raw)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var buffer = new ReadOnlySequence<byte>(bytes);
        var request = new HttpRequest();
        if (!HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _))
            throw new InvalidOperationException("Failed to parse complete request");
        return request;
    }
}
