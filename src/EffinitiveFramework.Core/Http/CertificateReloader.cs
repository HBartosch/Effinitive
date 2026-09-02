using System.Security.Cryptography.X509Certificates;
using EffinitiveFramework.Core.Configuration;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Keeps a listener's certificate current while the server runs, so a renewal
/// on disk is picked up without a restart.
/// </summary>
/// <remarks>
/// <para>
/// The certificate is resolved per handshake through
/// <see cref="System.Net.Security.SslServerAuthenticationOptions.ServerCertificateSelectionCallback"/>
/// rather than bound once into <c>ServerCertificate</c>. That is what makes a
/// swap take effect at all: options built at startup would otherwise hold the
/// original certificate for the lifetime of the process.
/// </para>
/// <para>
/// Connections already established keep the certificate they negotiated with.
/// TLS authenticates the peer once, at handshake, so there is nothing to
/// re-present on an open connection and nothing to interrupt.
/// </para>
/// </remarks>
internal sealed class CertificateReloader : IDisposable
{
    private readonly TlsOptions _options;
    private readonly Timer _timer;
    private readonly Action<Exception>? _onError;

    private X509Certificate2 _current;
    private (long Length, DateTime WriteUtc) _certStamp;
    private (long Length, DateTime WriteUtc) _keyStamp;

    internal CertificateReloader(TlsOptions options, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Certificate == null)
            throw new InvalidOperationException("Certificate must be loaded before reloading can be enabled.");

        _options = options;
        _onError = onError;
        _current = options.Certificate;
        _certStamp = Stamp(options.CertificatePath);
        _keyStamp = Stamp(options.KeyPath);

        var interval = options.ReloadPollInterval > TimeSpan.Zero
            ? options.ReloadPollInterval
            : TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>The certificate to present on the next handshake.</summary>
    internal X509Certificate2 Current => Volatile.Read(ref _current);

    private void Poll()
    {
        try
        {
            var cert = Stamp(_options.CertificatePath);
            var key = Stamp(_options.KeyPath);
            if (cert == _certStamp && key == _keyStamp)
                return;

            // A pair is two files, and a renewal writes both. Reading between
            // the two writes yields a certificate and key that do not match, so
            // the load is allowed to fail and is simply retried on the next
            // tick rather than being treated as a rotation.
            var replacement = Load();
            if (replacement == null)
                return;

            var previous = Interlocked.Exchange(ref _current, replacement);
            _certStamp = cert;
            _keyStamp = key;

            // Not disposed: a handshake in flight may still hold it. Letting the
            // finalizer reclaim it costs one certificate per rotation, which for
            // something that happens every few weeks is the cheaper mistake.
            _ = previous;
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }

    private X509Certificate2? Load()
    {
        try
        {
            if (string.IsNullOrEmpty(_options.CertificatePath))
                return null;

            return string.IsNullOrEmpty(_options.KeyPath)
                ? LoadPkcs12(_options.CertificatePath, _options.CertificatePassword)
                : X509Certificate2.CreateFromPemFile(_options.CertificatePath, _options.KeyPath);
        }
        catch
        {
            // Half-written pair, or a file briefly absent during a rename.
            return null;
        }
    }

    /// <summary>
    /// Load a PFX from disk. The <see cref="X509Certificate2"/> constructor that
    /// takes a path is obsolete from .NET 9 (SYSLIB0057) in favour of
    /// <c>X509CertificateLoader</c>, which does not exist on .NET 8, so the two
    /// targets take different routes to the same result.
    /// </summary>
    private static X509Certificate2 LoadPkcs12(string path, string? password)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
#else
        return new X509Certificate2(path, password);
#endif
    }

    private static (long, DateTime) Stamp(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (0, default);
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0, default);
        }
        catch
        {
            return (0, default);
        }
    }

    public void Dispose() => _timer.Dispose();
}
