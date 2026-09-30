using System.Net;
using System.Net.Sockets;
using System.Text;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
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
public class BodylessResponseFramingTests : IAsyncLifetime
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
    public async Task NotModified_CarriesNoContent()
    {
        var etag = await EtagOf("/a");

        using var client = Connect();
        var response = await Read(client,
            "GET /a HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: " + etag + "\r\n\r\n");

        Assert.Contains("304", StatusLine(response));
        Assert.Equal(string.Empty, BodyOf(response));
        Assert.DoesNotContain("alpha", response);
    }

    [Fact]
    public async Task NotModified_LeavesTheConnectionUsable()
    {
        var etag = await EtagOf("/a");

        using var client = Connect();
        await Read(client, "GET /a HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: " + etag + "\r\n\r\n");

        var next = await Read(client, "GET /b HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Contains("200", StatusLine(next));
        Assert.Contains("bravo", next);
    }

    [Fact]
    public async Task Head_CarriesNoContent()
    {
        using var client = Connect();

        var response = await Read(client, "HEAD /a HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Contains("200", StatusLine(response));
        Assert.Equal(string.Empty, BodyOf(response));
        Assert.DoesNotContain("alpha", response);
    }

    [Fact]
    public async Task Head_LeavesTheConnectionUsable()
    {
        using var client = Connect();
        await Read(client, "HEAD /a HTTP/1.1\r\nHost: localhost\r\n\r\n");

        var next = await Read(client, "GET /b HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Contains("200", StatusLine(next));
        Assert.Contains("bravo", next);
    }

    private async Task<string> EtagOf(string path)
    {
        using var client = Connect();
        var response = await Read(client, "GET " + path + " HTTP/1.1\r\nHost: localhost\r\n\r\n");

        foreach (var line in response.Split("\r\n"))
        {
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("ETag", StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        throw new InvalidOperationException("no ETag on:\n" + response);
    }

    private static string StatusLine(string response)
    {
        var end = response.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? response : response[..end];
    }

    private static string BodyOf(string response)
    {
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return split < 0 ? string.Empty : response[(split + 4)..];
    }

    private Socket Connect()
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = 4000
        };
        client.Connect(new IPEndPoint(IPAddress.Loopback, _port));
        return client;
    }

    // Reads one response, then waits briefly for anything the server should not have sent, so
    // that content following a bodyless status is caught rather than left for the next read.
    private static async Task<string> Read(Socket client, string request)
    {
        client.Send(Encoding.ASCII.GetBytes(request));

        var buffer = new byte[32 * 1024];
        var text = new StringBuilder();

        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await Task.Run(() => client.Receive(buffer));
            if (read == 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        await Task.Delay(120);
        while (client.Available > 0)
        {
            int read = client.Receive(buffer);
            if (read == 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
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
