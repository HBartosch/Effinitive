using System.Collections.Concurrent;

namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// In-process store of per-client token buckets, bounded by client count.
/// <para>
/// The bound is not an optimization, it is the point: a dictionary that grows one entry per distinct
/// source address is itself the denial-of-service vector rate limiting exists to prevent. An attacker
/// rotating source addresses — trivial over QUIC, and cheap from a botnet — would otherwise exhaust
/// memory faster than the limiter could reject them.
/// </para>
/// <para>
/// Eviction is cheap because of a property of the token bucket: a bucket idle for a full window has
/// refilled to capacity, which is identical to the state a new bucket starts in. Dropping it therefore
/// loses nothing and cannot let a client exceed its limit. Only when idle eviction is not enough does
/// the store fall back to evicting the least recently seen clients.
/// </para>
/// </summary>
public sealed class MemoryRateLimitStore : IRateLimitStore
{
    // Nested rather than keyed by a combined "policy\npartition" string: composing that key would
    // allocate on every request, and this hot path is meant to be allocation-free. The outer
    // dictionary holds a handful of policies, so the extra lookup is cheaper than the string it avoids.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TokenBucket>> _byPolicy =
        new(StringComparer.Ordinal);

    private readonly int _maxTrackedClients;
    private int _sweeping;

    public MemoryRateLimitStore(int maxTrackedClients = 100_000)
    {
        if (maxTrackedClients <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTrackedClients), "Client cap must be positive.");
        _maxTrackedClients = maxTrackedClients;
    }

    public int TrackedClients
    {
        get
        {
            var total = 0;
            foreach (var policy in _byPolicy)
                total += policy.Value.Count;
            return total;
        }
    }

    public bool TryAcquire(string partitionKey, RateLimitPolicy policy, long nowTicks, out int retryAfterSeconds, out int remaining)
    {
        // Each policy gets its own partition map, so an endpoint's tighter limit has its own allowance
        // rather than drawing down the global one twice.
        if (!_byPolicy.TryGetValue(policy.Name, out var buckets))
            buckets = _byPolicy.GetOrAdd(policy.Name, static _ => new ConcurrentDictionary<string, TokenBucket>(StringComparer.Ordinal));

        if (!buckets.TryGetValue(partitionKey, out var bucket))
        {
            var created = new TokenBucket(policy, nowTicks);
            bucket = buckets.GetOrAdd(partitionKey, created);

            // Only the thread whose bucket actually went in should consider sweeping.
            if (ReferenceEquals(bucket, created) && TrackedClients > _maxTrackedClients)
                Sweep(nowTicks);
        }

        return bucket.TryAcquire(nowTicks, out retryAfterSeconds, out remaining);
    }

    public void Clear() => _byPolicy.Clear();

    /// <summary>
    /// Brings the store back under its client cap. One sweep at a time; concurrent inserts that find a
    /// sweep in progress simply proceed and the overshoot is corrected by the next one.
    /// </summary>
    private void Sweep(long nowTicks)
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
            return;

        try
        {
            // Pass 1: buckets that have refilled to capacity are free to drop.
            foreach (var policy in _byPolicy)
            {
                foreach (var pair in policy.Value)
                {
                    if (pair.Value.IsDisposableAt(nowTicks))
                        policy.Value.TryRemove(new KeyValuePair<string, TokenBucket>(pair.Key, pair.Value));
                }
            }

            if (TrackedClients <= _maxTrackedClients)
                return;

            // Pass 2: still over budget, so drop the least recently seen clients across every policy.
            // This can forgive a client that was mid-limit, which is the right trade against unbounded
            // growth — and it only happens when more than MaxTrackedClients clients are active inside
            // one window.
            var ordered = _byPolicy
                .SelectMany(policy => policy.Value.Select(bucket => (Buckets: policy.Value, bucket.Key, bucket.Value)))
                .OrderBy(entry => entry.Value.LastAccessTicks)
                .ToArray();

            var target = _maxTrackedClients * 9 / 10;   // hysteresis, so we don't sweep on every insert
            for (int i = 0; i < ordered.Length && TrackedClients > target; i++)
                ordered[i].Buckets.TryRemove(new KeyValuePair<string, TokenBucket>(ordered[i].Key, ordered[i].Value));
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }
}
