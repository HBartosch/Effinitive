using EffinitiveFramework.Tests.Support;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Statuses and methods whose responses carry no content: 304 (RFC 9110 §15.4.5) and HEAD
/// (RFC 9110 §9.3.2).
/// </summary>
/// <remarks>
/// A response can hold its content as a byte array, a stream, an object still to be serialized,
/// or a stream handler. Dropping only the array leaves the writer to serialize the object, which
/// sends content under a status that promised none. Because such a response is terminated by the
/// empty line after its fields rather than by a length, the peer reads that content as the start
/// of the next response and everything after it on the connection is misread.
///
/// These assert the absence of content directly, and then that the connection still works, which
/// is the consequence that actually bites.
/// </remarks>
[Collection(RawHttpCollection.Name)]
public class BodylessResponseFramingTests
{
    private readonly RawHttpServerFixture _server;

    public BodylessResponseFramingTests(RawHttpServerFixture server) => _server = server;

    [Fact]
    public void NotModified_CarriesNoContent()
    {
        var etag = EtagOf("/a");

        using var client = new RawHttpClient(_server.Port);
        var response = client.Exchange(
            "GET /a HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: " + etag + "\r\n\r\n");

        Assert.Equal(304, response.StatusCode);
        Assert.Equal(string.Empty, response.Body);
        Assert.Equal(string.Empty, client.ReadUnexpectedTrailingBytes());
    }

    [Fact]
    public void NotModified_LeavesTheConnectionUsable()
    {
        var etag = EtagOf("/a");

        using var client = new RawHttpClient(_server.Port);
        client.Exchange("GET /a HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: " + etag + "\r\n\r\n");

        var next = client.Exchange("GET /b HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Equal(200, next.StatusCode);
        Assert.Contains("bravo", next.Body);
    }

    [Fact]
    public void Head_CarriesNoContent()
    {
        using var client = new RawHttpClient(_server.Port);

        var response = client.Exchange("HEAD /a HTTP/1.1\r\nHost: localhost\r\n\r\n", headRequest: true);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(string.Empty, response.Body);
        Assert.Equal(string.Empty, client.ReadUnexpectedTrailingBytes());
    }

    [Fact]
    public void Head_LeavesTheConnectionUsable()
    {
        using var client = new RawHttpClient(_server.Port);
        client.Exchange("HEAD /a HTTP/1.1\r\nHost: localhost\r\n\r\n", headRequest: true);

        var next = client.Exchange("GET /b HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Equal(200, next.StatusCode);
        Assert.Contains("bravo", next.Body);
    }

    private string EtagOf(string path)
    {
        using var client = new RawHttpClient(_server.Port);
        var response = client.Exchange("GET " + path + " HTTP/1.1\r\nHost: localhost\r\n\r\n");

        var etag = response.Header("ETag");
        Assert.False(string.IsNullOrEmpty(etag), "no ETag on " + response.StatusLine);
        return etag;
    }
}
