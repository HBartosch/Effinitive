namespace EffinitiveFramework.Core.Caching;

/// <summary>
/// Global configuration for <see cref="ResponseCacheMiddleware"/>. Per-endpoint behaviour is opt-in
/// via <see cref="ResponseCacheAttribute"/>.
/// </summary>
public sealed class ResponseCacheOptions
{
    /// <summary>
    /// Largest response body that will be stored, in bytes (default 1 MB). Larger responses are
    /// served normally but never cached, so one big payload can't evict everything else.
    /// </summary>
    public int MaxBodySizeBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Total memory budget for cached bodies, in bytes (default 100 MB). Exceeding it evicts expired
    /// entries first, then the oldest entries.
    /// </summary>
    public long MaxCacheSizeBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// Freshness lifetime used when <see cref="ResponseCacheAttribute.Duration"/> is left at 0
    /// (default 60 seconds).
    /// </summary>
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Status codes eligible for caching (default: 200 only). Everything else is passed through.
    /// </summary>
    public int[] CacheableStatusCodes { get; set; } = new[] { 200 };

    /// <summary>
    /// Storage backend. Left null, the middleware creates a <see cref="MemoryResponseCache"/> sized by
    /// <see cref="MaxCacheSizeBytes"/> and assigns it here, so a caller that captures the options
    /// instance can read <see cref="IResponseCache.Hits"/> / <see cref="IResponseCache.SizeBytes"/>
    /// after startup, or plug in a custom implementation before it.
    /// </summary>
    public IResponseCache? Store { get; set; }
}
