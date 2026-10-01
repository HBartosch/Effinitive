using System.Net;
using System.Net.Sockets;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using Xunit;

namespace EffinitiveFramework.Tests.Support;

/// <summary>
/// One server on one port, shared by the tests that drive a real socket.
/// </summary>
/// <remarks>
/// Binding port 0, reading the port back and closing the socket before the server binds it leaves
/// a window in which another test can be handed the same port. When that happened the losing
/// server failed to bind, its failure went unobserved because it is started without awaiting, and
/// its tests then talked to the other server. One server on one port removes the window, and the
/// collection below keeps the classes that use it from running at the same time.
/// </remarks>
public sealed class RawHttpServerFixture : IAsyncLifetime
{
    public int Port { get; private set; }

    private EffinitiveServer _server = null!;
    private CancellationTokenSource _cts = null!;

    public async Task InitializeAsync()
    {
        var router = new Router();
        router.AddRoute("GET", "/a", (EmptyRequest _, CancellationToken _) => Task.FromResult("alpha"));
        router.AddRoute("GET", "/b", (EmptyRequest _, CancellationToken _) => Task.FromResult("bravo"));
        router.AddRoute("POST", "/echo", (EmptyRequest _, CancellationToken _) => Task.FromResult("posted"));
        router.Freeze();

        _cts = new CancellationTokenSource();

        // Retried rather than assumed: the port is chosen by binding and releasing, so another
        // process can take it in between. A bind failure surfaces here instead of as a test that
        // cannot connect.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Port = FreePort();
            _server = new EffinitiveServer(
                new ServerOptions { HttpPort = Port, EnableDebugLogging = false }, router);

            _ = _server.StartAsync(_cts.Token);

            if (await WaitUntilListening())
                return;
        }

        throw new InvalidOperationException("test server never started listening");
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task<bool> WaitUntilListening()
    {
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.Connect(new IPEndPoint(IPAddress.Loopback, Port));
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        return false;
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}

/// <summary>
/// Groups the socket-driven tests so they share one server and do not run concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RawHttpCollection : ICollectionFixture<RawHttpServerFixture>
{
    public const string Name = "raw-http";
}
