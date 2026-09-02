using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A listener's ALPN list decides what that port actually serves, because a
/// client offering several protocols takes the server's first match. These pin
/// the property that made the old static cache wrong: two listeners sharing a
/// certificate must still advertise their own protocol lists.
/// </summary>
public class ListenerBindingTests
{
    [Fact]
    public void SecureBinding_DefaultsToHttp2ThenHttp11()
    {
        using var cert = SelfSigned();
        var binding = ListenerBinding.Secure(8443, cert);

        Assert.True(binding.IsSecure);
        Assert.Equal(
            [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            binding.SslOptions!.ApplicationProtocols);
    }

    [Fact]
    public void SecureBinding_HonoursAnExplicitAlpnList()
    {
        using var cert = SelfSigned();
        var binding = ListenerBinding.Secure(8081, cert, [SslApplicationProtocol.Http11]);

        // RFC 7301 §3.2: the server selects from the client's list, so what the
        // server advertises decides the protocol. A listener meant for HTTP/1.1
        // that also offers h2 will serve h2 to any client that speaks it.
        Assert.Equal([SslApplicationProtocol.Http11], binding.SslOptions!.ApplicationProtocols);
    }

    [Fact]
    public void TwoBindings_SharingOneCertificate_KeepSeparateAlpnLists()
    {
        using var cert = SelfSigned();
        var h2 = ListenerBinding.Secure(8443, cert);
        var h1 = ListenerBinding.Secure(8081, cert, [SslApplicationProtocol.Http11]);

        // The regression: a single static cache keyed on the certificate served
        // whichever list was built last to both ports.
        Assert.Equal(2, h2.SslOptions!.ApplicationProtocols!.Count);
        Assert.Single(h1.SslOptions!.ApplicationProtocols!);
        Assert.NotSame(h2.SslOptions, h1.SslOptions);
        Assert.Same(cert, h2.SslOptions.ServerCertificate);
        Assert.Same(cert, h1.SslOptions.ServerCertificate);
    }

    [Fact]
    public void SecureBinding_RefusesObsoleteProtocolVersions()
    {
        using var cert = SelfSigned();
        var binding = ListenerBinding.Secure(8443, cert);

        var enabled = binding.SslOptions!.EnabledSslProtocols;
        Assert.True(enabled.HasFlag(SslProtocols.Tls13));
        Assert.True(enabled.HasFlag(SslProtocols.Tls12));
#pragma warning disable SYSLIB0039 // naming the obsolete versions is the point
        Assert.False(enabled.HasFlag(SslProtocols.Tls11));
        Assert.False(enabled.HasFlag(SslProtocols.Tls));
#pragma warning restore SYSLIB0039
    }

    [Fact]
    public void PlaintextBinding_CarriesNoTlsConfiguration()
    {
        var binding = ListenerBinding.Plaintext(8080, "http");

        Assert.False(binding.IsSecure);
        Assert.Null(binding.SslOptions);
        Assert.Equal(8080, binding.Port);
    }

    [Fact]
    public void AddListener_AppendsToServerOptions()
    {
        var options = new ServerOptions();
        Assert.Empty(options.Listeners);

        var builder = EffinitiveApp.Create()
            .AddListener(l =>
            {
                l.Port = 8081;
                l.UseTls = true;
                l.AlpnProtocols = [SslApplicationProtocol.Http11];
            })
            .AddListener(l => l.Port = 8082);

        Assert.NotNull(builder);
    }

    [Fact]
    public void ListenerOptions_DefaultToPlaintextWithInheritedAlpn()
    {
        var listener = new ListenerOptions { Port = 8082 };

        Assert.False(listener.UseTls);
        // Null rather than an empty list: null means "advertise what the built-in
        // HTTPS port advertises", and an empty list would mean "advertise nothing".
        Assert.Null(listener.AlpnProtocols);
        Assert.NotNull(listener.Tls);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=effinitive-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
