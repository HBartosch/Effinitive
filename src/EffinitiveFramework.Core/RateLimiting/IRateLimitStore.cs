namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// Holds per-client allowances. The default implementation is <see cref="MemoryRateLimitStore"/>;
/// a custom one can be supplied through <see cref="RateLimitOptions.Store"/> — for example to share
/// limits across a cluster.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Attempts to spend one permit for <paramref name="partitionKey"/> under <paramref name="policy"/>.
    /// </summary>
    /// <param name="partitionKey">Identifies the client, typically its IP address.</param>
    /// <param name="policy">The limit being applied; different policies never share an allowance.</param>
    /// <param name="nowTicks">Current UTC tick count, passed in so callers and tests control time.</param>
    /// <param name="retryAfterSeconds">When denied, whole seconds until a permit frees up.</param>
    /// <param name="remaining">Permits left after the attempt.</param>
    bool TryAcquire(string partitionKey, RateLimitPolicy policy, long nowTicks, out int retryAfterSeconds, out int remaining);

    /// <summary>Number of client allowances currently held.</summary>
    int TrackedClients { get; }

    /// <summary>Drops all tracked allowances.</summary>
    void Clear();
}
