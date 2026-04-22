using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for the pre-routing request validation logic in EffinitiveServer.RequestValidation.cs.
/// Covers HTTP version checks, Host header requirements, method+body restrictions,
/// Expect header handling, and Accept content negotiation.
/// </summary>
public class RequestValidationTests
{
    private readonly EffinitiveServer _server;

    public RequestValidationTests()
    {
        var options = new ServerOptions { HttpPort = 0, EnableDebugLogging = false };
        var router = new Router();
        _server = new EffinitiveServer(options, router);
    }

    // ── HTTP version checks ──

    [Fact]
    public void ValidateRequest_Http11_Continues()
    {
        var request = MakeRequest(httpVersion: "HTTP/1.1");
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    [Fact]
    public void ValidateRequest_Http10_WithHost_Continues()
    {
        var request = MakeRequest(httpVersion: "HTTP/1.0");
        request.Headers["Host"] = "localhost";
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    [Theory]
    [InlineData("HTTP/1.2")]
    [InlineData("HTTP/2.0")]
    [InlineData("HTTP/0.9")]
    public void ValidateRequest_UnsupportedVersion_ClosesConnection(string version)
    {
        var request = MakeRequest(httpVersion: version);
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.CloseConnection, result.Action);
    }

    // ── Host header checks ──

    [Fact]
    public void ValidateRequest_Http10_NoHost_Returns400()
    {
        var request = MakeRequest(httpVersion: "HTTP/1.0");
        request.Headers.Remove("Host");
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.CloseConnection, result.Action);
        Assert.NotNull(result.Response);
        Assert.Equal(400, result.Response!.StatusCode);
    }

    // ── Absolute-form URI host mismatch ──

    [Fact]
    public void ValidateRequest_AbsoluteFormHostMismatch_Returns400()
    {
        var request = MakeRequest();
        request.Headers["Host"] = "example.com";
        request.Items = new Dictionary<string, object> { ["AbsoluteFormHost"] = "evil.com" };
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.CloseConnection, result.Action);
        Assert.Equal(400, result.Response!.StatusCode);
    }

    [Fact]
    public void ValidateRequest_AbsoluteFormHostMatch_Continues()
    {
        var request = MakeRequest();
        request.Headers["Host"] = "example.com:8080";
        request.Items = new Dictionary<string, object> { ["AbsoluteFormHost"] = "example.com:80" };
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    // ── Range DoS protection ──

    [Fact]
    public void ValidateRequest_ExcessiveRanges_ClosesConnection()
    {
        var request = MakeRequest();
        request.Headers["Range"] = "bytes=" + string.Join(",", Enumerable.Range(0, 200).Select(i => $"{i}-{i + 1}"));
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.CloseConnection, result.Action);
        Assert.Null(result.Response); // silent close
    }

    // ── GET/HEAD/OPTIONS with body rejection ──

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ValidateRequest_SafeMethodWithBody_Returns400(string method)
    {
        var request = MakeRequest(method: method);
        request.ContentLength = 100;
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.CloseConnection, result.Action);
        Assert.Equal(400, result.Response!.StatusCode);
    }

    [Fact]
    public void ValidateRequest_PostWithBody_Continues()
    {
        var request = MakeRequest(method: "POST");
        request.ContentLength = 100;
        request.Body = new byte[100];
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    // ── POST with CL:0 sets KeepAlive = false ──

    [Fact]
    public void ValidateRequest_PostCL0_SetsKeepAliveFalse()
    {
        var request = MakeRequest(method: "POST");
        request.ContentLength = 0;
        Assert.True(request.KeepAlive);
        _server.ValidateRequest(request);
        Assert.False(request.KeepAlive);
    }

    // ── HTTP/1.0 Connection: close default ──

    [Fact]
    public void ValidateRequest_Http10_NoConnectionHeader_SetsKeepAliveFalse()
    {
        var request = MakeRequest(httpVersion: "HTTP/1.0");
        request.Headers["Host"] = "localhost";
        Assert.True(request.KeepAlive);
        _server.ValidateRequest(request);
        Assert.False(request.KeepAlive);
    }

    [Fact]
    public void ValidateRequest_Http10_ConnectionKeepAlive_PreservesKeepAlive()
    {
        var request = MakeRequest(httpVersion: "HTTP/1.0");
        request.Headers["Host"] = "localhost";
        request.Headers["Connection"] = "keep-alive";
        _server.ValidateRequest(request);
        Assert.True(request.KeepAlive);
    }

    // ── Expect header ──

    [Fact]
    public void ValidateRequest_UnknownExpect_Returns417()
    {
        var request = MakeRequest();
        request.Headers["Expect"] = "200-ok";
        var result = _server.ValidateRequest(request);
        Assert.NotNull(result.Response);
        Assert.Equal(417, result.Response!.StatusCode);
    }

    [Fact]
    public void ValidateRequest_Expect100Continue_Continues()
    {
        var request = MakeRequest();
        request.Headers["Expect"] = "100-continue";
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    [Fact]
    public void ValidateRequest_Expect100ContinueWithBodyAlreadySent_Continues()
    {
        // Curl and other clients legitimately send body alongside Expect: 100-continue
        var request = MakeRequest(method: "POST");
        request.Headers["Expect"] = "100-continue";
        request.ContentLength = 10;
        request.Body = new byte[10];
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    // ── Accept content negotiation ──

    [Theory]
    [InlineData("*/*")]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("text/plain, application/xml")]
    public void ValidateRequest_SupportedAccept_Continues(string accept)
    {
        var request = MakeRequest();
        request.Headers["Accept"] = accept;
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    [Fact]
    public void ValidateRequest_UnsupportedAccept_Returns406()
    {
        var request = MakeRequest();
        request.Headers["Accept"] = "image/webp";
        var result = _server.ValidateRequest(request);
        Assert.NotNull(result.Response);
        Assert.Equal(406, result.Response!.StatusCode);
    }

    [Fact]
    public void ValidateRequest_NoAcceptHeader_Continues()
    {
        var request = MakeRequest();
        var result = _server.ValidateRequest(request);
        Assert.Equal(EffinitiveServer.ValidationAction.Continue, result.Action);
    }

    // ── Helper ──

    private static HttpRequest MakeRequest(string method = "GET", string path = "/", string httpVersion = "HTTP/1.1")
    {
        var req = new HttpRequest
        {
            Method = method,
            Path = path,
            HttpVersion = httpVersion
        };
        req.Headers["Host"] = "localhost";
        return req;
    }
}
