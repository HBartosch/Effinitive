using System.Net;

namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// Configuration for <c>UseRateLimiting()</c>.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Burst capacity of the server-wide limit — requests a client may spend at once.
    /// </summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Time taken for the server-wide allowance to refill from empty. With the defaults, a client may
    /// burst 100 requests and then sustain about 1.7 per second.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum number of distinct clients tracked at once. Reaching it evicts idle allowances first and
    /// then the least recently seen clients, so memory stays bounded no matter how many source
    /// addresses appear.
    /// </summary>
    public int MaxTrackedClients { get; set; } = 100_000;

    /// <summary>Status code returned when a client is over its limit.</summary>
    public int RejectionStatusCode { get; set; } = 429;

    /// <summary>
    /// Whether rejections carry <c>X-RateLimit-Limit</c> / <c>-Remaining</c> / <c>-Reset</c> alongside
    /// the standard <c>Retry-After</c>. These describe how the limiter is configured, so turn them off
    /// if you would rather not advertise that.
    /// </summary>
    public bool EmitRateLimitHeaders { get; set; } = true;

    /// <summary>
    /// Whether to take the client address from <c>X-Forwarded-For</c> instead of the socket.
    /// <para>
    /// <b>Off by default, and deliberately so.</b> The header is supplied by the caller, so trusting it
    /// unconditionally lets anyone mint a fresh allowance per request simply by varying the value —
    /// which would make the limiter worse than useless. Enable this only when the server is genuinely
    /// behind a reverse proxy, and populate <see cref="TrustedProxies"/> so the header is only believed
    /// when the connection actually came from that proxy.
    /// </para>
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Peer addresses whose <c>X-Forwarded-For</c> header is believed. Only consulted when
    /// <see cref="TrustForwardedHeaders"/> is true; if it is true and this is empty, the header is
    /// ignored, because a trusted-proxy setup with no trusted proxies configured is a misconfiguration
    /// rather than an invitation to trust everyone.
    /// </summary>
    public List<IPAddress> TrustedProxies { get; } = new();

    /// <summary>
    /// Backing store. Left null, a <see cref="MemoryRateLimitStore"/> sized by
    /// <see cref="MaxTrackedClients"/> is created and assigned here, so a caller that captures the
    /// options can inspect <see cref="IRateLimitStore.TrackedClients"/> afterwards or plug in their own.
    /// </summary>
    public IRateLimitStore? Store { get; set; }

    /// <summary>Adds a trusted reverse proxy address and enables forwarded-header handling.</summary>
    public RateLimitOptions AddTrustedProxy(string address)
    {
        TrustedProxies.Add(IPAddress.Parse(address));
        TrustForwardedHeaders = true;
        return this;
    }
}
