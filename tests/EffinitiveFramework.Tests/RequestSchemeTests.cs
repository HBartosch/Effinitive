using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Whether a request arrived over TLS is a property of the connection it arrived on, so on
/// HTTP/1.1 there is nowhere else for <see cref="HttpRequest.IsHttps"/> to come from. HTTP/2 and
/// HTTP/3 read it off the :scheme pseudo-header instead (RFC 9113 §8.3.1).
/// </summary>
/// <remarks>
/// The property was public and never assigned on the HTTP/1.1 path, so an application that
/// checked it was told the request was plaintext even when it had arrived over TLS. Nothing
/// failed loudly, which is what made it worth pinning: an answer that is wrong in only one
/// direction looks like it works until something depends on it.
/// </remarks>
public class RequestSchemeTests : IAsyncLifetime
{
    public sealed class SchemeEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/scheme";

        // "no-context" is distinguished deliberately. Folding it into "plain" would let a null
        // request masquerade as a correct answer for the plaintext case.
        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult(
                HttpContext is null ? "no-context" : HttpContext.IsHttps ? "secure" : "plain");
    }

    public sealed class PeerEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/peer";

        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult(
                HttpContext is null ? "no-context" : HttpContext.RemoteIpAddress?.ToString() ?? "none");
    }

    private EffinitiveServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private X509Certificate2 _certificate = null!;
    private int _httpPort;
    private int _httpsPort;

    public async Task InitializeAsync()
    {
        _certificate = SelfSigned();

        var router = new Router();
        router.AddEndpointType("GET", "/scheme", typeof(SchemeEndpoint), EndpointInvoker.Build(typeof(SchemeEndpoint)));
        router.AddEndpointType("GET", "/peer", typeof(PeerEndpoint), EndpointInvoker.Build(typeof(PeerEndpoint)));
        router.Freeze();

        var services = new ServiceCollection();
        services.AddTransient(typeof(SchemeEndpoint), typeof(SchemeEndpoint));
        services.AddTransient(typeof(PeerEndpoint), typeof(PeerEndpoint));
        var provider = services.BuildServiceProvider();

        _cts = new CancellationTokenSource();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            _httpPort = FreePort();
            _httpsPort = FreePort();
            if (_httpPort == _httpsPort) continue;

            _server = new EffinitiveServer(
                new ServerOptions
                {
                    HttpPort = _httpPort,
                    HttpsPort = _httpsPort,
                    TlsOptions = { Certificate = _certificate },
                    EnableDebugLogging = false
                },
                router,
                provider);

            _ = _server.StartAsync(_cts.Token);

            if (await Listening(_httpPort) && await Listening(_httpsPort))
                return;
        }

        throw new InvalidOperationException("test server never started listening on both ports");
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        _certificate.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OverPlaintext_TheRequestIsNotReportedAsSecure()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _httpPort);

        Assert.Equal("\"plain\"", await ExchangeAsync(client.GetStream()));
    }

    [Fact]
    public async Task OverTls_TheRequestIsReportedAsSecure()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _httpsPort);

        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" });

        Assert.Equal("\"secure\"", await ExchangeAsync(tls));
    }

    // The peer address is stamped on the request the same way the scheme is, and was cleared by
    // the same reset, so it is pinned alongside it. Rate limiting partitions on this value, so a
    // null here puts every client in one bucket.
    [Fact]
    public async Task ThePeerAddressReachesTheRequest()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _httpPort);

        Assert.Equal("\"127.0.0.1\"", await ExchangeAsync(client.GetStream(), "/peer"));
    }

    /// <summary>Sends one request and returns exactly the content the framing describes.</summary>
    private static async Task<string> ExchangeAsync(Stream stream, string path = "/scheme")
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        await stream.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[16 * 1024];
        var received = new List<byte>();

        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0) throw new IOException("closed before the field block completed");
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var headerText = Encoding.ASCII.GetString(received.GetRange(0, headerEnd).ToArray());
        int length = 0;
        foreach (var line in headerText.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(colon + 1)..].Trim(), out length);
        }

        int bodyStart = headerEnd + 4;
        while (received.Count - bodyStart < length)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        int available = Math.Max(0, Math.Min(length, received.Count - bodyStart));
        return Encoding.ASCII.GetString(received.GetRange(bodyStart, available).ToArray());
    }

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (int i = 0; i + 3 < bytes.Count; i++)
        {
            if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n'
                && bytes[i + 2] == (byte)'\r' && bytes[i + 3] == (byte)'\n')
                return i;
        }

        return -1;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // On Windows the private key must round-trip through a PFX before SslStream will use it
        // for a server handshake. Without this the handshake ends in an unexplained EOF.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static async Task<bool> Listening(int port)
    {
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
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
