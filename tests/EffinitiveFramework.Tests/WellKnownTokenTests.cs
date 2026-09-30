using System.Buffers;
using System.Text;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A method, an HTTP version and a header field name come from small known sets, so the parser
/// stores the canonical instance it already holds rather than decoding the bytes into a new
/// string. These assert reference identity with that instance, because value equality would
/// pass just as well against a freshly decoded copy and so would not show that anything was
/// saved. They also assert that anything outside those sets still parses.
/// </summary>
public class WellKnownTokenTests
{
    private static HttpRequest Parse(string raw)
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(raw));
        var request = new HttpRequest();
        Assert.True(HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _, 1024 * 1024));
        return request;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public void KnownMethod_IsTheCanonicalInstance(string method)
    {
        var request = Parse($"{method} / HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Equal(method, request.Method);
        Assert.Same(string.Intern(method), request.Method);
    }

    [Fact]
    public void KnownVersion_IsTheCanonicalInstance()
    {
        var request = Parse("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Same(HttpVersions.Http11, request.HttpVersion);
    }

    [Fact]
    public void KnownHeaderName_IsTheCanonicalInstance()
    {
        var request = Parse(
            "GET / HTTP/1.1\r\nHost: localhost\r\nUser-Agent: probe\r\nAccept-Encoding: gzip\r\n\r\n");

        Assert.Same(HeaderNames.Host, KeyOf(request, HeaderNames.Host));
        Assert.Same(HeaderNames.UserAgent, KeyOf(request, HeaderNames.UserAgent));
        Assert.Same(HeaderNames.AcceptEncoding, KeyOf(request, HeaderNames.AcceptEncoding));
    }

    // RFC 9110 §5.1: field names are case-insensitive, so whatever spelling arrives, the
    // canonical one is what gets stored. The dictionary compares with OrdinalIgnoreCase,
    // so no lookup changes either way.
    [Theory]
    [InlineData("host")]
    [InlineData("HOST")]
    [InlineData("HoSt")]
    public void HeaderName_MatchesRegardlessOfCase(string spelling)
    {
        var request = Parse($"GET / HTTP/1.1\r\n{spelling}: localhost\r\n\r\n");

        Assert.Same(HeaderNames.Host, KeyOf(request, HeaderNames.Host));
        Assert.Equal("localhost", request.Headers[HeaderNames.Host]);
    }

    // RFC 9110 §9.1: the method is case-sensitive, so "get" is not the same token as GET.
    // The parser rejects a lowercase method outright, which is why matching one exactly
    // cannot fold it onto GET.
    [Fact]
    public void LowercaseMethod_IsRejectedRatherThanFoldedOntoGet()
    {
        Assert.Throws<HttpParseException>(
            () => Parse("get / HTTP/1.1\r\nHost: localhost\r\n\r\n"));
    }

    [Fact]
    public void UnknownHeaderName_KeepsItsOwnSpellingAndValue()
    {
        var request = Parse("GET / HTTP/1.1\r\nHost: localhost\r\nX-Custom-Thing: 42\r\n\r\n");

        Assert.Equal("42", request.Headers["X-Custom-Thing"]);
        Assert.Equal("X-Custom-Thing", KeyOf(request, "X-Custom-Thing"));
    }

    [Fact]
    public void UnknownMethod_StillParses()
    {
        var request = Parse("PROPFIND / HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Equal("PROPFIND", request.Method);
    }

    [Fact]
    public void NameThatSharesALengthWithAKnownOne_IsNotMistakenForIt()
    {
        // Same length as Host, differing in one byte.
        var request = Parse("GET / HTTP/1.1\r\nHost: localhost\r\nHoot: owl\r\n\r\n");

        Assert.Equal("owl", request.Headers["Hoot"]);
        Assert.Equal("Hoot", KeyOf(request, "Hoot"));
        Assert.Equal("localhost", request.Headers[HeaderNames.Host]);
    }

    private static string KeyOf(HttpRequest request, string name)
    {
        foreach (var key in request.Headers.Keys)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return key;

        throw new Xunit.Sdk.XunitException($"header {name} not present");
    }
}
