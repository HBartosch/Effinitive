using System.Net;
using System.Net.Sockets;
using System.Text;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Handling a request on the receiving thread, and the default that does not.
/// </summary>
/// <remarks>
/// The option moves parsing and handling onto the thread the bytes arrived on. Those threads are
/// few and each runs one piece of work at a time, so a handler that blocks one stops every
/// connection that queue owns. That is why the default leaves handling on the pool, where a
/// blocked handler costs a pool thread the pool can replace, and why the default is worth a test
/// of its own: it is a safety property, not a preference.
/// </remarks>
public class InlineRequestProcessingTests
{
    [Fact]
    public void HandlingStaysOnThePoolUnlessItIsAskedFor()
    {
        Assert.False(new ServerOptions().InlineRequestProcessing);
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<string> GetAsync(int port, string path)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));

        await socket.SendAsync(
            Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"),
            SocketFlags.None);

        var received = new List<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (read == 0) break;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return Encoding.ASCII.GetString([.. received]);
    }

    private static async Task<(EffinitiveServer server, CancellationTokenSource cts, int port)> StartAsync(
        bool inline)
    {
        var router = new Router();
        router.AddRoute("GET", "/sum", (EmptyRequest _, CancellationToken _) => Task.FromResult("55"));
        router.AddRoute("GET", "/slow", async (EmptyRequest _, CancellationToken ct) =>
        {
            await Task.Delay(20, ct);
            return "late";
        });
        router.Freeze();

        int port = FreePort();
        var cts = new CancellationTokenSource();
        var server = new EffinitiveServer(
            new ServerOptions
            {
                HttpPort = port,
                EnableDebugLogging = false,
                InlineRequestProcessing = inline,
            },
            router);

        await server.StartAsync(cts.Token);
        return (server, cts, port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestIsAnsweredTheSameWayEitherWay(bool inline)
    {
        var (server, cts, port) = await StartAsync(inline);
        try
        {
            var response = await GetAsync(port, "/sum");

            Assert.StartsWith("HTTP/1.1 200", response);
            Assert.Contains("55", response);
        }
        finally
        {
            await cts.CancelAsync();
            await server.StopAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandlerThatAwaitsStillCompletes(bool inline)
    {
        // Awaiting releases the thread, so a handler that yields is safe on either scheduler.
        // It is a handler that blocks without yielding that the default exists to tolerate.
        var (server, cts, port) = await StartAsync(inline);
        try
        {
            var response = await GetAsync(port, "/slow");

            Assert.StartsWith("HTTP/1.1 200", response);
            Assert.Contains("late", response);
        }
        finally
        {
            await cts.CancelAsync();
            await server.StopAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRequestsAreAllAnswered(bool inline)
    {
        var (server, cts, port) = await StartAsync(inline);
        try
        {
            var responses = await Task.WhenAll(
                Enumerable.Range(0, 24).Select(_ => GetAsync(port, "/sum")));

            Assert.All(responses, r => Assert.StartsWith("HTTP/1.1 200", r));
            Assert.All(responses, r => Assert.Contains("55", r));
        }
        finally
        {
            await cts.CancelAsync();
            await server.StopAsync(TimeSpan.FromSeconds(2));
        }
    }
}
