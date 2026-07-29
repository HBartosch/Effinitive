using System.Diagnostics.CodeAnalysis;

namespace EffinitiveFramework.Core.Caching;

/// <summary>
/// An immutable snapshot of a cacheable response.
/// <para>
/// Instances are shared by reference across concurrent requests, so nothing here may ever be mutated
/// after construction — in particular <see cref="Body"/> is handed straight to the response writer,
/// which only reads it.
/// </para>
/// </summary>
public sealed class CachedResponse
{
    /// <summary>Cached status code (always one of <see cref="ResponseCacheOptions.CacheableStatusCodes"/>).</summary>
    public int StatusCode { get; }

    /// <summary>Content-Type of the cached representation.</summary>
    public string ContentType { get; }

    /// <summary>The serialized, uncompressed response body. Never mutated.</summary>
    public byte[] Body { get; }

    /// <summary>
    /// Response headers to replay, excluding Content-Type/Content-Length/Connection which the writer
    /// generates. Stored as an array because it is only ever enumerated.
    /// </summary>
    public KeyValuePair<string, string>[] Headers { get; }

    /// <summary>Strong ETag computed from <see cref="Body"/>, used to answer If-None-Match on a hit.</summary>
    public string ETag { get; }

    /// <summary>UTC ticks at which this entry was stored — the basis for the <c>Age</c> header.</summary>
    public long StoredAtTicks { get; }

    /// <summary>UTC ticks at which this entry becomes stale.</summary>
    public long ExpiresAtTicks { get; }

    /// <summary>Approximate memory footprint, used for the cache size budget.</summary>
    public long SizeBytes { get; }

    public CachedResponse(
        int statusCode,
        string contentType,
        byte[] body,
        KeyValuePair<string, string>[] headers,
        string etag,
        long storedAtTicks,
        long expiresAtTicks)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
        Headers = headers;
        ETag = etag;
        StoredAtTicks = storedAtTicks;
        ExpiresAtTicks = expiresAtTicks;

        // Body dominates; add a rough per-header allowance so header-heavy entries aren't free.
        long size = body.Length + contentType.Length + etag.Length + 64;
        for (int i = 0; i < headers.Length; i++)
            size += headers[i].Key.Length + headers[i].Value.Length + 32;
        SizeBytes = size;
    }

    /// <summary>Whether this entry is still fresh at the given UTC tick count.</summary>
    public bool IsFresh(long utcTicks) => utcTicks < ExpiresAtTicks;
}

/// <summary>
/// Storage backend for <see cref="ResponseCacheMiddleware"/>. The default implementation is
/// <see cref="MemoryResponseCache"/>; a custom one can be supplied via
/// <see cref="ResponseCacheOptions.Store"/>.
/// </summary>
public interface IResponseCache
{
    /// <summary>
    /// Looks up a fresh entry. Expired entries must be reported as a miss (and may be dropped).
    /// Implementations are responsible for updating <see cref="Hits"/> / <see cref="Misses"/>.
    /// </summary>
    bool TryGet(string key, [NotNullWhen(true)] out CachedResponse? entry);

    /// <summary>Stores an entry, evicting as needed to stay within the configured size budget.</summary>
    void Set(string key, CachedResponse entry);

    /// <summary>
    /// Invalidates every cached representation of <paramref name="path"/> (RFC 9111 §4.4), called
    /// after a successful unsafe method on that path.
    /// </summary>
    void InvalidatePath(string path);

    /// <summary>
    /// Current invalidation generation for <paramref name="path"/>. The middleware folds this into the
    /// cache key so a bump orphans every prior entry for the path in O(1).
    /// </summary>
    long GetPathGeneration(string path);

    /// <summary>Number of lookups served from the cache.</summary>
    long Hits { get; }

    /// <summary>Number of lookups that had to run the endpoint.</summary>
    long Misses { get; }

    /// <summary>Number of entries currently stored (including any not yet swept).</summary>
    int EntryCount { get; }

    /// <summary>Approximate bytes currently held.</summary>
    long SizeBytes { get; }
}
