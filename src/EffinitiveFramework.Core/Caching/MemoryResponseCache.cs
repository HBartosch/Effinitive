using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace EffinitiveFramework.Core.Caching;

/// <summary>
/// In-process response cache backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> with a total
/// size budget.
/// <para>
/// Expiry is evaluated lazily on read; eviction runs only when a store would push the cache over
/// budget, dropping expired entries first and then the oldest ones. Path invalidation is O(1): rather
/// than maintaining a path→keys index, each path carries a generation counter that the cache key
/// embeds, so bumping it orphans every prior entry for that path at once. Orphaned entries hold memory
/// until they expire or a sweep reclaims them.
/// </para>
/// </summary>
public sealed class MemoryResponseCache : IResponseCache
{
    // Cache keys are built by the middleware and compared exactly — ordinal is both correct and fastest.
    private readonly ConcurrentDictionary<string, CachedResponse> _entries = new(StringComparer.Ordinal);

    // Paths match the router's case-insensitive semantics.
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);

    private readonly long _maxSizeBytes;

    private long _sizeBytes;
    private long _hits;
    private long _misses;
    private int _sweeping;

    public MemoryResponseCache(long maxSizeBytes = 100L * 1024 * 1024)
    {
        if (maxSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSizeBytes), "Cache size budget must be positive.");
        _maxSizeBytes = maxSizeBytes;
    }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public int EntryCount => _entries.Count;

    public long SizeBytes => Interlocked.Read(ref _sizeBytes);

    public bool TryGet(string key, [NotNullWhen(true)] out CachedResponse? entry)
    {
        if (_entries.TryGetValue(key, out var found))
        {
            if (found.IsFresh(DateTime.UtcNow.Ticks))
            {
                Interlocked.Increment(ref _hits);
                entry = found;
                return true;
            }

            // Stale — drop it now so the memory is reclaimed without waiting for a sweep.
            Remove(key, found);
        }

        Interlocked.Increment(ref _misses);
        entry = null;
        return false;
    }

    public void Set(string key, CachedResponse entry)
    {
        // A single entry larger than the whole budget would immediately evict everything else.
        if (entry.SizeBytes > _maxSizeBytes)
            return;

        // Remove-then-add rather than AddOrUpdate: the update factory can run more than once under
        // contention, which would double-count the size adjustment.
        if (_entries.TryRemove(key, out var previous))
            Interlocked.Add(ref _sizeBytes, -previous.SizeBytes);

        if (_entries.TryAdd(key, entry))
            Interlocked.Add(ref _sizeBytes, entry.SizeBytes);

        if (Interlocked.Read(ref _sizeBytes) > _maxSizeBytes)
            Sweep();
    }

    public void InvalidatePath(string path)
        => _generations.AddOrUpdate(path, 1L, static (_, generation) => generation + 1);

    public long GetPathGeneration(string path)
    {
        // Apps that never write never pay for the lookup.
        if (_generations.IsEmpty)
            return 0;
        return _generations.TryGetValue(path, out var generation) ? generation : 0;
    }

    /// <summary>
    /// Drops every cached entry. Invalidation generations are preserved so keys already in flight
    /// don't become valid again.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _sizeBytes, 0);
    }

    private void Remove(string key, CachedResponse expected)
    {
        // Conditional remove: only reclaim the size we actually took out, in case another thread
        // replaced the entry in between.
        if (_entries.TryRemove(new KeyValuePair<string, CachedResponse>(key, expected)))
            Interlocked.Add(ref _sizeBytes, -expected.SizeBytes);
    }

    /// <summary>
    /// Brings the cache back under budget. Only one sweep runs at a time; concurrent stores that find
    /// a sweep in progress simply proceed, and the overshoot is corrected by the next one.
    /// </summary>
    private void Sweep()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
            return;

        try
        {
            var nowTicks = DateTime.UtcNow.Ticks;

            // Pass 1: expired entries are free to drop.
            foreach (var pair in _entries)
            {
                if (!pair.Value.IsFresh(nowTicks))
                    Remove(pair.Key, pair.Value);
            }

            if (Interlocked.Read(ref _sizeBytes) <= _maxSizeBytes)
                return;

            // Pass 2: oldest-first until comfortably under budget, so we don't sweep again on the
            // very next store.
            var target = (long)(_maxSizeBytes * 0.9);
            var ordered = _entries.ToArray();
            Array.Sort(ordered, static (a, b) => a.Value.StoredAtTicks.CompareTo(b.Value.StoredAtTicks));

            for (int i = 0; i < ordered.Length && Interlocked.Read(ref _sizeBytes) > target; i++)
                Remove(ordered[i].Key, ordered[i].Value);
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }
}
