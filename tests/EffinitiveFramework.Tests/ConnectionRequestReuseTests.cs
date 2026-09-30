using System.Net;
using System.Net.Sockets;
using System.Text;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
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
public class ConnectionRequestReuseTests : IAsyncLifetime
{
    private EffinitiveServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = FreePort();

        var router = new Router();
        router.AddRoute("GET", "/a", (EmptyRequest _, CancellationToken _) => Task.FromResult("alpha"));
        router.AddRoute("GET", "/b", (EmptyRequest _, CancellationToken _) => Task.FromResult("bravo"));
        router.AddRoute("POST", "/echo", (EmptyRequest _, CancellationToken _) => Task.FromResult("posted"));
        router.Freeze();

        _server = new EffinitiveServer(
            new ServerOptions { HttpPort = _port, EnableDebugLogging = false }, router);

        _cts = new CancellationTokenSource();
        _ = _server.StartAsync(_cts.Token);
        await WaitUntilListening();
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SequentialRequestsOnOneConnection_EachGetItsOwnResponse()
    {
        using var client = Connect();

        var first = await Exchange(client, Get("/a"));
        var second = await Exchange(client, Get("/b"));
        var third = await Exchange(client, Get("/a"));

        Assert.Contains("alpha", first);
        Assert.Contains("bravo", second);
        Assert.Contains("alpha", third);
        Assert.DoesNotContain("bravo", third);
    }

    [Fact]
    public async Task PipelinedRequestsInOneWrite_AreAnsweredInOrder()
    {
        using var client = Connect();

        var all = await Exchange(client, Get("/a") + Get("/b") + Get("/a"), expectedResponses: 3);

        var alpha = all.IndexOf("alpha", StringComparison.Ordinal);
        var bravo = all.IndexOf("bravo", StringComparison.Ordinal);
        var alphaAgain = alpha < 0 ? -1 : all.IndexOf("alpha", alpha + 1, StringComparison.Ordinal);

        Assert.True(alpha >= 0 && bravo > alpha && alphaAgain > bravo,
            "responses out of order or missing:\n" + all);
    }

    // The sharpest probe available from outside: a conditional request that is answered 304 leaves
    // If-None-Match in the header dictionary. If the reset misses it, the next request on the same
    // connection is answered 304 for a header its client never sent.
    [Fact]
    public async Task ConditionalHeaderFromOneRequest_DoesNotAffectTheNext()
    {
        using var client = Connect();

        var plain = await Exchange(client, Get("/a"));
        var etag = HeaderValue(plain, "ETag");
        Assert.False(string.IsNullOrEmpty(etag), "no ETag on:\n" + plain);

        var conditional = await Exchange(client,
            "GET /a HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: " + etag + "\r\n\r\n");
        Assert.Contains("304", StatusLine(conditional));

        var afterwards = await Exchange(client, Get("/a"));
        Assert.Contains("200", StatusLine(afterwards));
        Assert.Contains("alpha", afterwards);
    }

    [Fact]
    public async Task BodyFromOneRequest_DoesNotLeakIntoTheNext()
    {
        using var client = Connect();

        const string body = "{\"value\":\"payload\"}";
        var posted = await Exchange(client,
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" +
            "Content-Length: " + body.Length + "\r\n\r\n" + body);
        Assert.Contains("posted", posted);

        var afterwards = await Exchange(client, Get("/a"));
        Assert.Contains("200", StatusLine(afterwards));
        Assert.Contains("alpha", afterwards);
        Assert.DoesNotContain("payload", afterwards);
    }

    [Fact]
    public async Task ASecondConnectionIsNotAffectedByTheFirst()
    {
        using (var first = Connect())
        {
            var response = await Exchange(first, Get("/b"));
            Assert.Contains("bravo", response);
        }

        using var second = Connect();
        var onFresh = await Exchange(second, Get("/a"));

        Assert.Contains("alpha", onFresh);
        Assert.DoesNotContain("bravo", onFresh);
    }

    private static string Get(string path) => "GET " + path + " HTTP/1.1\r\nHost: localhost\r\n\r\n";

    private static string StatusLine(string response)
    {
        var end = response.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? response : response[..end];
    }

    private static string HeaderValue(string response, string name)
    {
        foreach (var line in response.Split("\r\n"))
        {
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return string.Empty;
    }

    private Socket Connect()
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = 5000
        };
        client.Connect(new IPEndPoint(IPAddress.Loopback, _port));
        return client;
    }

    private static async Task<string> Exchange(Socket client, string request, int expectedResponses = 1)
    {
        client.Send(Encoding.ASCII.GetBytes(request));

        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();

        // Read until every expected status line has arrived rather than for a fixed number of
        // reads, so a response split across segments does not read as a missing one.
        while (CountOccurrences(text.ToString(), "HTTP/1.1 ") < expectedResponses)
        {
            int read = await Task.Run(() => client.Receive(buffer));
            if (read == 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private async Task WaitUntilListening()
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Connect(new IPEndPoint(IPAddress.Loopback, _port));
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("server did not start listening on port " + _port);
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
