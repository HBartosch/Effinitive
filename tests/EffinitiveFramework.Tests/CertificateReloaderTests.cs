using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A certificate renewed on disk has to reach the next handshake without a
/// restart. These drive the reloader directly rather than through a socket, so
/// what is asserted is the swap itself and not TLS.
/// </summary>
public class CertificateReloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eff-cert-tests").FullName;

    [Fact]
    public async Task ReplacingThePair_IsPickedUp()
    {
        var options = WritePair("first");
        options.LoadCertificate();
        var original = options.Certificate!.Thumbprint;

        using var reloader = new CertificateReloader(options);
        Assert.Equal(original, reloader.Current.Thumbprint);

        WritePair("second");
        var swapped = await WaitFor(reloader, original);

        Assert.True(swapped, "the replacement pair was not picked up within the window");
        Assert.NotEqual(original, reloader.Current.Thumbprint);
    }

    [Fact]
    public async Task AHalfWrittenPair_DoesNotReplaceAWorkingCertificate()
    {
        var options = WritePair("first");
        options.LoadCertificate();
        var original = options.Certificate!.Thumbprint;

        using var reloader = new CertificateReloader(options);

        // A renewal writes two files, and a poll can land between them. The
        // certificate that results does not match the key, so it must be
        // rejected and retried rather than served.
        File.WriteAllText(Path.Combine(_dir, "server.crt"), "-----BEGIN CERTIFICATE-----\nnonsense\n-----END CERTIFICATE-----\n");
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(original, reloader.Current.Thumbprint);
    }

    [Fact]
    public async Task AnUnchangedPair_IsNotReloaded()
    {
        var options = WritePair("first");
        options.LoadCertificate();

        using var reloader = new CertificateReloader(options);
        var before = reloader.Current;
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Same instance, not merely an equal one: re-reading an untouched file
        // every second would churn a certificate per tick for no reason.
        Assert.Same(before, reloader.Current);
    }

    [Fact]
    public void ReloadingRequiresALoadedCertificate()
    {
        var options = new TlsOptions { ReloadOnChange = true };
        Assert.Throws<InvalidOperationException>(() => new CertificateReloader(options));
    }

    private static async Task<bool> WaitFor(CertificateReloader reloader, string original)
    {
        for (int i = 0; i < 40; i++)
        {
            if (reloader.Current.Thumbprint != original)
                return true;
            await Task.Delay(250);
        }
        return false;
    }

    private TlsOptions WritePair(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

        var certPath = Path.Combine(_dir, "server.crt");
        var keyPath = Path.Combine(_dir, "server.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        return new TlsOptions
        {
            CertificatePath = certPath,
            KeyPath = keyPath,
            ReloadOnChange = true,
            ReloadPollInterval = TimeSpan.FromMilliseconds(250),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
