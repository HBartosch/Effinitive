using System.Net;
using System.Net.Sockets;
using System.Text;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// The cancellation token a WebSocket handler is given belongs to its own connection, and is
/// still cancelled when the server stops.
/// </summary>
/// <remarks>
/// A handler passes its token to every read and every send, and a pipe operation that suspends
/// registers a callback against the source behind that token. One source shared by every
/// connection therefore collects a registration per message from every connection at once, and
/// the lock protecting its registration list is then taken on the path each message must follow.
///
/// Both halves matter. Separate sources are what keep one connection's registrations away from
/// another's, and the link back to the server's token is what still lets a shutdown unblock a
/// handler parked on a read.
/// </remarks>
public class WebSocketHandlerCancellationTests
{
    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>Completes the upgrade and leaves the socket open, as a client would.</summary>
    private static async Task<Socket> UpgradeAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));

        var request =
            "GET /ws HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None);

        var buffer = new byte[1024];
        int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
        var response = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.StartsWith("HTTP/1.1 101", response);

        return socket;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task EachConnectionIsGivenItsOwnTokenAndAShutdownStillCancelsThem()
    {
        var tokens = new List<CancellationToken>();
        var gate = new object();

        var router = new Router();
        router.AddWebSocketRoute("/ws", async (ws, ct) =>
        {
            lock (gate) tokens.Add(ct);
            // Park until the server stops, which is what the second assertion observes.
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
        });
        router.Freeze();

        int port = FreePort();
        using var serverCancellation = new CancellationTokenSource();
        var server = new EffinitiveServer(
            new ServerOptions { HttpPort = port, EnableDebugLogging = false }, router);
        _ = server.StartAsync(serverCancellation.Token);

        Socket? first = null, second = null;
        try
        {
            Assert.True(await WaitUntil(() =>
            {
                try
                {
                    using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    probe.Connect(new IPEndPoint(IPAddress.Loopback, port));
                    return true;
                }
                catch (SocketException) { return false; }
            }), "the test server never started listening");

            first = await UpgradeAsync(port);
            second = await UpgradeAsync(port);

            Assert.True(await WaitUntil(() => { lock (gate) return tokens.Count == 2; }),
                "both handlers should have been entered");

            CancellationToken a, b;
            lock (gate) { a = tokens[0]; b = tokens[1]; }

            Assert.False(a.Equals(b),
                "both connections were handed the same cancellation token, so every registration " +
                "either one makes lands on a single shared registration list");

            Assert.False(a.IsCancellationRequested);
            Assert.False(b.IsCancellationRequested);

            // Stopping is what cancels connection work; the token handed to StartAsync only
            // governs the caller's own wait.
            _ = server.StopAsync(TimeSpan.FromSeconds(2));

            Assert.True(await WaitUntil(() => a.IsCancellationRequested && b.IsCancellationRequested),
                "stopping the server left a handler's token uncancelled, so a handler parked on a " +
                "read would never be released");
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
            if (!serverCancellation.IsCancellationRequested)
                await serverCancellation.CancelAsync();
        }
    }
}
