using EffinitiveFramework.Tests.Support;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A connection parses every request it serves into one <see cref="Core.Http.HttpRequest"/>,
/// resetting it before each. The failure that buys is state from one request being visible to
/// the next: a path that still routes to the previous endpoint, a header the client did not
/// send this time, a body that belonged to the request before.
/// </summary>
/// <remarks>
/// None of that is observable from a single request, so these drive a real socket and send
/// several, both one after another and pipelined into a single write, then check each response
/// against the request that should have produced it.
/// </remarks>
[Collection(RawHttpCollection.Name)]
public class ConnectionRequestReuseTests
{
    private readonly RawHttpServerFixture _server;

    public ConnectionRequestReuseTests(RawHttpServerFixture server) => _server = server;

    [Fact]
    public void SequentialRequestsOnOneConnection_EachGetItsOwnResponse()
    {
        using var client = new RawHttpClient(_server.Port);

        var first = client.Exchange(Get("/a"));
        var second = client.Exchange(Get("/b"));
        var third = client.Exchange(Get("/a"));

        Assert.Contains("alpha", first.Body);
        Assert.Contains("bravo", second.Body);
        Assert.Contains("alpha", third.Body);
        Assert.DoesNotContain("bravo", third.Body);
    }

    [Fact]
    public void PipelinedRequestsInOneWrite_AreAnsweredInOrder()
    {
        using var client = new RawHttpClient(_server.Port);

        client.Send(Get("/a") + Get("/b") + Get("/a"));

        var first = client.ReadResponse();
        var second = client.ReadResponse();
        var third = client.ReadResponse();

        Assert.Contains("alpha", first.Body);
        Assert.Contains("bravo", second.Body);
        Assert.Contains("alpha", third.Body);
    }

    // The sharpest probe available from outside: a conditional request that is answered 304 leaves
    // If-None-Match in the header dictionary. If the reset misses it, the next request on the same
    // connection is answered 304 for a header its client never sent.
    [Fact]
    public void ConditionalHeaderFromOneRequest_DoesNotAffectTheNext()
    {
        using var client = new RawHttpClient(_server.Port);

        var plain = client.Exchange(Get("/a"));
        var etag = plain.Header("ETag");
        Assert.False(string.IsNullOrEmpty(etag), "no ETag on " + plain.StatusLine);

        var conditional = client.Exchange(
            "GET /a HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: " + etag + "\r\n\r\n");
        Assert.Equal(304, conditional.StatusCode);

        var afterwards = client.Exchange(Get("/a"));

        Assert.Equal(200, afterwards.StatusCode);
        Assert.Contains("alpha", afterwards.Body);
    }

    [Fact]
    public void BodyFromOneRequest_DoesNotLeakIntoTheNext()
    {
        using var client = new RawHttpClient(_server.Port);

        const string body = "{\"value\":\"payload\"}";
        var posted = client.Exchange(
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" +
            "Content-Length: " + body.Length + "\r\n\r\n" + body);
        Assert.Contains("posted", posted.Body);

        var afterwards = client.Exchange(Get("/a"));

        Assert.Equal(200, afterwards.StatusCode);
        Assert.Contains("alpha", afterwards.Body);
        Assert.DoesNotContain("payload", afterwards.Body);
    }

    [Fact]
    public void ASecondConnectionIsNotAffectedByTheFirst()
    {
        using (var first = new RawHttpClient(_server.Port))
        {
            Assert.Contains("bravo", first.Exchange(Get("/b")).Body);
        }

        using var second = new RawHttpClient(_server.Port);
        var onFresh = second.Exchange(Get("/a"));

        Assert.Contains("alpha", onFresh.Body);
        Assert.DoesNotContain("bravo", onFresh.Body);
    }

    private static string Get(string path) => "GET " + path + " HTTP/1.1\r\nHost: localhost\r\n\r\n";
}
