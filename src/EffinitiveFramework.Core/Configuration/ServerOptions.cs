using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EffinitiveFramework.Core.Configuration;

/// <summary>
/// TLS/HTTPS configuration options
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// Server certificate for HTTPS
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// Path to certificate file (PFX)
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Certificate password
    /// </summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Load certificate from path if configured
    /// </summary>
    public void LoadCertificate()
    {
        if (Certificate == null && !string.IsNullOrEmpty(CertificatePath))
        {
            Certificate = new X509Certificate2(CertificatePath, CertificatePassword);
        }
    }
}

/// <summary>
/// Server configuration options
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// HTTP port (default: 5000)
    /// </summary>
    public int HttpPort { get; set; } = 5000;

    /// <summary>
    /// HTTPS port (default: 5001), 0 to disable
    /// </summary>
    public int HttpsPort { get; set; } = 0;

    /// <summary>
    /// Maximum concurrent connections (default: CPU count * 100)
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = Environment.ProcessorCount * 100;

    /// <summary>
    /// Idle connection timeout (default: 120 seconds)
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Request header timeout (default: 30 seconds)
    /// </summary>
    public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum request body size in bytes (default: 30MB)
    /// Protects against unbounded memory allocation DoS attacks
    /// </summary>
    public int MaxRequestBodySize { get; set; } = 30 * 1024 * 1024; // 30MB

    /// <summary>
    /// Request timeout to prevent Slowloris attacks (default: 30 seconds)
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of pushed streams per HTTP/2 connection (default: 10)
    /// Prevents DoS via unlimited server push
    /// </summary>
    public int MaxPushedStreamsPerConnection { get; set; } = 10;

    /// <summary>
    /// Maximum size of a single pushed resource in bytes (default: 1MB)
    /// Prevents bandwidth waste on oversized pushes
    /// </summary>
    public int MaxPushedResourceSize { get; set; } = 1024 * 1024; // 1MB

    /// <summary>
    /// JSON serialization options
    /// </summary>
    public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>
    /// TLS options for HTTPS
    /// </summary>
    public TlsOptions TlsOptions { get; set; } = new TlsOptions();
}

/// <summary>
/// Server metrics
/// </summary>
public sealed class ServerMetrics
{
    private long _totalRequests;
    private int _activeConnections;
    private DateTime _startTime = DateTime.UtcNow;

    /// <summary>
    /// Total number of requests processed
    /// </summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    /// <summary>
    /// Currently active connections
    /// </summary>
    public int ActiveConnections => _activeConnections;

    /// <summary>
    /// Requests per second (approximate)
    /// </summary>
    public double RequestsPerSecond
    {
        get
        {
            var elapsed = DateTime.UtcNow - _startTime;
            return elapsed.TotalSeconds > 0 ? TotalRequests / elapsed.TotalSeconds : 0;
        }
    }

    /// <summary>
    /// Server uptime
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    internal void IncrementRequests() => Interlocked.Increment(ref _totalRequests);
    internal void IncrementConnections() => Interlocked.Increment(ref _activeConnections);
    internal void DecrementConnections() => Interlocked.Decrement(ref _activeConnections);
}
