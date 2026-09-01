using System.Net.Security;

namespace EffinitiveFramework.Core.Configuration;

/// <summary>
/// One additional socket for the server to accept on, beyond
/// <see cref="ServerOptions.HttpPort"/> and <see cref="ServerOptions.HttpsPort"/>.
/// </summary>
/// <remarks>
/// <para>
/// A listener carries its own certificate and its own ALPN list, which is the
/// point of the type. The two built-in ports share one TLS configuration, so
/// they cannot express "HTTP/2 and HTTP/1.1 on 8443, but HTTP/1.1 only on
/// 8081", nor serve two ports from different certificate files.
/// </para>
/// <para>
/// Each listener resolves its certificate and builds its
/// <see cref="SslServerAuthenticationOptions"/> once at startup, so nothing is
/// negotiated or allocated per connection.
/// </para>
/// </remarks>
public sealed class ListenerOptions
{
    /// <summary>TCP port to bind. Required.</summary>
    public int Port { get; set; }

    /// <summary>Terminate TLS on this listener. Requires a certificate in <see cref="Tls"/>.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Treat every connection as cleartext HTTP/2 with prior knowledge
    /// (RFC 9113 §3.3), reading the client connection preface immediately
    /// rather than parsing an HTTP/1.1 request or negotiating over ALPN.
    /// </summary>
    /// <remarks>
    /// This is a property of the port, not of the request, because prior
    /// knowledge means exactly that the client sends the preface without
    /// asking. A listener with this set does not serve HTTP/1.1, so it needs a
    /// port of its own rather than sharing one with <see cref="ServerOptions.HttpPort"/>.
    /// </remarks>
    public bool UseHttp2Cleartext { get; set; }

    /// <summary>Certificate source for this listener, independent of every other listener.</summary>
    public TlsOptions Tls { get; set; } = new();

    /// <summary>
    /// Protocols to advertise over ALPN, in preference order. Null advertises
    /// HTTP/2 then HTTP/1.1, matching the built-in HTTPS port.
    /// <para>
    /// Set this to HTTP/1.1 alone for a listener that must not be upgraded to
    /// HTTP/2: a client offering both takes the server's first match, so a
    /// listener that advertises h2 will serve h2 whether or not that was the
    /// intent.
    /// </para>
    /// </summary>
    public IReadOnlyList<SslApplicationProtocol>? AlpnProtocols { get; set; }

    /// <summary>Human-readable name used in startup logging. Defaults to the port.</summary>
    public string? Name { get; set; }
}
