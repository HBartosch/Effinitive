using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// RFC 8446 §6.1 requires a close_notify alert before the write side closes,
/// so that a peer can tell a complete response from a truncated one. These
/// establish where in a teardown that alert can still be sent.
/// </summary>
public class TlsShutdownOrderingTests
{
    [Fact]
    public void CompletingAStreamPipeReader_DisposesTheStream_WhenLeaveOpenIsFalse()
    {
        var stream = new TrackingStream();
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: false));

        reader.Complete();

        Assert.True(stream.Disposed,
            "PipeReader.Complete() did not dispose the inner stream, so the premise under test does not hold");
    }

    [Fact]
    public void CompletingAStreamPipeWriter_DisposesTheStream_WhenLeaveOpenIsFalse()
    {
        var stream = new TrackingStream();
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: false));

        writer.Complete();

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ShutdownAfterCompletingThePipes_CannotSendTheAlert()
    {
        await using var pair = await TlsPair.CreateAsync();

        // The ordering under test: complete the pipes, then try to say goodbye.
        var reader = PipeReader.Create(pair.Server, new StreamPipeReaderOptions(leaveOpen: false));
        var writer = PipeWriter.Create(pair.Server, new StreamPipeWriterOptions(leaveOpen: false));
        reader.Complete();
        writer.Complete();

        var thrown = await Record.ExceptionAsync(() => pair.Server.ShutdownAsync());

        Assert.IsType<ObjectDisposedException>(thrown);
    }

    [Fact]
    public async Task ShutdownBeforeCompletingThePipes_SendsTheAlert()
    {
        await using var pair = await TlsPair.CreateAsync();

        var reader = PipeReader.Create(pair.Server, new StreamPipeReaderOptions(leaveOpen: false));
        var writer = PipeWriter.Create(pair.Server, new StreamPipeWriterOptions(leaveOpen: false));

        // Say goodbye while the stream is still live, then tear down.
        var thrown = await Record.ExceptionAsync(() => pair.Server.ShutdownAsync());
        reader.Complete();
        writer.Complete();

        Assert.Null(thrown);

        // A client that receives close_notify reads a clean end of stream. One
        // whose peer simply vanished gets an IOException instead, which is the
        // truncation it cannot distinguish from a short response.
        var buffer = new byte[16];
        var read = await pair.Client.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task HttpConnection_SendsCloseNotify_OnTheTlsHttp11Path()
    {
        using var cert = TlsPair.SelfSignedCertificate();

        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = clientSocket.ConnectAsync(endpoint);
        var serverSocket = await listener.AcceptAsync();
        await connect;

        var connection = new EffinitiveFramework.Core.Http.HttpConnection();
        await using var client = new SslStream(
            new NetworkStream(clientSocket, ownsSocket: false), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, _, _, _) => true);

        // http/1.1 only, so the connection takes the stream-pipe path where the
        // alert was previously lost.
        var serverInit = connection.InitializeAsync(
            serverSocket, isSecure: true, cert, CancellationToken.None);
        var clientAuth = client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols = [SslApplicationProtocol.Http11],
        });
        await Task.WhenAll(serverInit, clientAuth).WaitAsync(TimeSpan.FromSeconds(30));

        await connection.DisposeAsync();

        // close_notify received: a clean end of stream rather than an IOException.
        var buffer = new byte[16];
        var read = await client.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, read);

        listener.Dispose();
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    /// <summary>A connected, authenticated SslStream pair over loopback TCP.</summary>
    private sealed class TlsPair : IAsyncDisposable
    {
        public required SslStream Server { get; init; }
        public required SslStream Client { get; init; }
        public required Socket Listener { get; init; }

        public static async Task<TlsPair> CreateAsync()
        {
            using var cert = SelfSignedCertificate();

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connect = clientSocket.ConnectAsync(endpoint);
            var serverSocket = await listener.AcceptAsync();
            await connect;

            var server = new SslStream(new NetworkStream(serverSocket, ownsSocket: true), leaveInnerStreamOpen: false);
            var client = new SslStream(
                new NetworkStream(clientSocket, ownsSocket: true), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            var serverAuth = server.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
            });
            var clientAuth = client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
            });
            await Task.WhenAll(serverAuth, clientAuth).WaitAsync(TimeSpan.FromSeconds(30));

            return new TlsPair { Server = server, Client = client, Listener = listener };
        }

        public static X509Certificate2 SelfSignedCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // On Windows the private key must round-trip through a PFX before
            // SslStream will use it for a server handshake.
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); } catch { }
            try { await Server.DisposeAsync(); } catch { }
            Listener.Dispose();
        }
    }
}
