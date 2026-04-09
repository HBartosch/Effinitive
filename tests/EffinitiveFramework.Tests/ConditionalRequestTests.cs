using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for WeakETagMatch (RFC 9110 §8.8.3.2) and ApplyConditionalHeaders (§13.1).
/// </summary>
public class ConditionalRequestTests
{
    private readonly EffinitiveServer _server;

    public ConditionalRequestTests()
    {
        var options = new ServerOptions { HttpPort = 0, EnableDebugLogging = false };
        var router = new Router();
        _server = new EffinitiveServer(options, router);
    }

    // ── WeakETagMatch ──

    [Fact]
    public void WeakETagMatch_Wildcard_ReturnsTrue()
    {
        Assert.True(EffinitiveServer.WeakETagMatch("*", "\"abc123\""));
    }

    [Fact]
    public void WeakETagMatch_ExactMatch_ReturnsTrue()
    {
        Assert.True(EffinitiveServer.WeakETagMatch("\"abc123\"", "\"abc123\""));
    }

    [Fact]
    public void WeakETagMatch_WeakPrefix_StripsAndMatches()
    {
        Assert.True(EffinitiveServer.WeakETagMatch("W/\"abc123\"", "\"abc123\""));
        Assert.True(EffinitiveServer.WeakETagMatch("\"abc123\"", "W/\"abc123\""));
        Assert.True(EffinitiveServer.WeakETagMatch("W/\"abc123\"", "W/\"abc123\""));
    }

    [Fact]
    public void WeakETagMatch_CommaSeparated_FindsMatch()
    {
        Assert.True(EffinitiveServer.WeakETagMatch("\"aaa\", \"bbb\", \"ccc\"", "\"bbb\""));
    }

    [Fact]
    public void WeakETagMatch_NoMatch_ReturnsFalse()
    {
        Assert.False(EffinitiveServer.WeakETagMatch("\"abc\"", "\"xyz\""));
    }

    [Fact]
    public void WeakETagMatch_UnquotedETag_ReturnsFalse()
    {
        Assert.False(EffinitiveServer.WeakETagMatch("abc123", "\"abc123\""));
    }

    [Fact]
    public void WeakETagMatch_InvalidResponseETag_ReturnsFalse()
    {
        Assert.False(EffinitiveServer.WeakETagMatch("\"abc\"", "not-quoted"));
    }

    // ── ApplyConditionalHeaders ──

    [Fact]
    public void ApplyConditionalHeaders_GetWith200_AddsETagAndLastModified()
    {
        var request = MakeRequest("GET");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1, 2, 3 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.True(response.Headers.ContainsKey("ETag"));
        Assert.True(response.Headers.ContainsKey("Last-Modified"));
        Assert.StartsWith("\"", response.Headers["ETag"]);
    }

    [Fact]
    public void ApplyConditionalHeaders_PostRequest_DoesNotAddHeaders()
    {
        var request = MakeRequest("POST");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1, 2, 3 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.False(response.Headers.ContainsKey("ETag"));
        Assert.False(response.Headers.ContainsKey("Last-Modified"));
    }

    [Fact]
    public void ApplyConditionalHeaders_204Response_SkipsHeaders()
    {
        var request = MakeRequest("GET");
        var response = new HttpResponse { StatusCode = 204 };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.False(response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public void ApplyConditionalHeaders_404Response_SkipsHeaders()
    {
        var request = MakeRequest("GET");
        var response = new HttpResponse { StatusCode = 404, Body = new byte[] { 1 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.False(response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public void ApplyConditionalHeaders_IfNoneMatch_MatchingETag_Returns304()
    {
        var request = MakeRequest("GET");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1, 2, 3 } };

        // First call to generate ETag
        _server.ApplyConditionalHeaders(request, response, isHead: false);
        var etag = response.Headers["ETag"];

        // Second request with matching If-None-Match
        var request2 = MakeRequest("GET");
        request2.Headers["If-None-Match"] = etag;
        var response2 = new HttpResponse { StatusCode = 200, Body = new byte[] { 1, 2, 3 } };

        _server.ApplyConditionalHeaders(request2, response2, isHead: false);

        Assert.Equal(304, response2.StatusCode);
        Assert.Null(response2.Body);
        Assert.Equal(etag, response2.Headers["ETag"]);
    }

    [Fact]
    public void ApplyConditionalHeaders_IfNoneMatch_NonMatching_Stays200()
    {
        var request = MakeRequest("GET");
        request.Headers["If-None-Match"] = "\"nonexistent\"";
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1, 2, 3 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void ApplyConditionalHeaders_IfModifiedSince_FutureDate_Stays200()
    {
        var request = MakeRequest("GET");
        request.Headers["If-Modified-Since"] = DateTime.UtcNow.AddHours(1).ToString("R");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void ApplyConditionalHeaders_IfModifiedSince_PastDate_Returns304()
    {
        var request = MakeRequest("GET");
        // Use current time (server start time is truncated to seconds, so UtcNow is >= start)
        request.Headers["If-Modified-Since"] = DateTime.UtcNow.ToString("R");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.Equal(304, response.StatusCode);
    }

    [Fact]
    public void ApplyConditionalHeaders_IfNoneMatchTakesPrecedence_OverIfModifiedSince()
    {
        var request = MakeRequest("GET");
        request.Headers["If-None-Match"] = "\"nonexistent\"";
        request.Headers["If-Modified-Since"] = DateTime.UtcNow.ToString("R");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1 } };

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        // If-None-Match doesn't match → 200 (If-Modified-Since should be ignored)
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void ApplyConditionalHeaders_PreservesExistingETag()
    {
        var request = MakeRequest("GET");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1 } };
        response.Headers["ETag"] = "\"custom-etag\"";

        _server.ApplyConditionalHeaders(request, response, isHead: false);

        Assert.Equal("\"custom-etag\"", response.Headers["ETag"]);
    }

    [Fact]
    public void ApplyConditionalHeaders_HeadRequest_AppliesHeaders()
    {
        var request = MakeRequest("HEAD");
        var response = new HttpResponse { StatusCode = 200, Body = new byte[] { 1 } };

        _server.ApplyConditionalHeaders(request, response, isHead: true);

        Assert.True(response.Headers.ContainsKey("ETag"));
        Assert.True(response.Headers.ContainsKey("Last-Modified"));
    }

    // ── Helper ──

    private static HttpRequest MakeRequest(string method = "GET")
    {
        var req = new HttpRequest { Method = method, Path = "/", HttpVersion = "HTTP/1.1" };
        req.Headers["Host"] = "localhost";
        return req;
    }
}
