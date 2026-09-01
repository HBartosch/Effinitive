using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// A listening socket's resolved configuration, built once at startup and
/// handed to every connection accepted on that socket.
/// </summary>
/// <remarks>
/// The TLS options are built here rather than on first use. The previous
/// arrangement cached one <see cref="SslServerAuthenticationOptions"/> in a
/// static field keyed on the certificate, which was correct only while there
/// was a single TLS listener: two listeners sharing a certificate but
/// advertising different ALPN lists would each invalidate the other's entry,
/// and a connection could be authenticated against whichever list happened to
/// be cached. Giving each listener its own instance removes the cache, the
/// lock, and that whole class of bug.
/// </remarks>
internal sealed class ListenerBinding
{
    private static readonly SslApplicationProtocol[] DefaultAlpn =
        [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11];

    public required int Port { get; init; }
    public required bool IsSecure { get; init; }

    /// <summary>Null on a plaintext listener.</summary>
    public SslServerAuthenticationOptions? SslOptions { get; init; }

    /// <summary>Used in startup logging only.</summary>
    public string Name { get; init; } = string.Empty;

    public static ListenerBinding Plaintext(int port, string name = "") =>
        new() { Port = port, IsSecure = false, Name = name };

    public static ListenerBinding Secure(
        int port,
        X509Certificate2 certificate,
        IReadOnlyList<SslApplicationProtocol>? alpn = null,
        string name = "") =>
        new()
        {
            Port = port,
            IsSecure = true,
            Name = name,
            SslOptions = BuildSslOptions(certificate, alpn),
        };

    internal static SslServerAuthenticationOptions BuildSslOptions(
        X509Certificate2 certificate,
        IReadOnlyList<SslApplicationProtocol>? alpn) =>
        new()
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            // A client offering both protocols takes the server's first match,
            // so this list decides what the listener actually serves.
            ApplicationProtocols = [.. alpn ?? DefaultAlpn],
            AllowTlsResume = true,
        };
}
