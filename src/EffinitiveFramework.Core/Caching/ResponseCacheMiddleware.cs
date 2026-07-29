using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;

namespace EffinitiveFramework.Core.Caching;

/// <summary>
/// Serves repeat GET/HEAD requests from an in-process store, skipping endpoint execution, DI scope
/// creation, and JSON serialization entirely, and emits the <c>Cache-Control</c> / <c>Vary</c> /
/// <c>Age</c> headers that let client and proxy caches participate.
/// <para>
/// Caching is <b>opt-in per endpoint</b>: nothing is stored unless the endpoint type carries
/// <see cref="ResponseCacheAttribute"/>, so adding this middleware to an existing app changes no
/// behaviour on its own.
/// </para>
/// <para>
/// <b>Ordering.</b> Register this <i>after</i> <c>UseResponseCompression()</c> so compression stays
/// outermost. The cache stores uncompressed bytes and the compression middleware re-applies
/// <c>Content-Encoding</c> on every hit; the other order would return past the compression middleware
/// and serve cache hits uncompressed.
/// </para>
/// </summary>
public sealed class ResponseCacheMiddleware : IMiddleware
{
    private readonly ResponseCacheOptions _options;
    private readonly IResponseCache _cache;

    // Reflecting the attribute once per endpoint type, not once per request.
    private readonly ConcurrentDictionary<Type, CachePolicy?> _policies = new();

    public ResponseCacheMiddleware(ResponseCacheOptions? options = null)
    {
        _options = options ?? new ResponseCacheOptions();

        // Assign the store back onto the options so the caller that configured them keeps a handle on
        // it for hit/miss statistics.
        _cache = _options.Store ??= new MemoryResponseCache(_options.MaxCacheSizeBytes);
    }

    /// <summary>The backing store, exposed for diagnostics.</summary>
    public IResponseCache Cache => _cache;

    public ValueTask<HttpResponse> InvokeAsync(HttpRequest request, RequestDelegate next, CancellationToken cancellationToken)
    {
        var method = request.Method;

        // A write to a path invalidates every cached representation of it (RFC 9111 §4.4).
        if (IsUnsafeMethod(method))
            return InvalidateAfterAsync(request, next, cancellationToken);

        if (!IsCacheableMethod(method))
            return next(request, cancellationToken);

        var policy = ResolvePolicy(request);
        if (policy == null)
            return next(request, cancellationToken);

        return InvokeWithPolicyAsync(request, next, policy, cancellationToken);
    }

    private async ValueTask<HttpResponse> InvalidateAfterAsync(HttpRequest request, RequestDelegate next, CancellationToken cancellationToken)
    {
        var response = await next(request, cancellationToken);

        // Only a write that actually took effect invalidates; a 4xx/5xx changed nothing.
        if (response.StatusCode >= 200 && response.StatusCode < 400)
            _cache.InvalidatePath(PathWithoutQuery(request.Path));

        return response;
    }

    private async ValueTask<HttpResponse> InvokeWithPolicyAsync(
        HttpRequest request,
        RequestDelegate next,
        CachePolicy policy,
        CancellationToken cancellationToken)
    {
        // Not storable (Location=Client/None, or NoStore) — headers only, and never consult the store.
        // Credentialed requests are also excluded unless the endpoint explicitly allowed it, so a
        // per-user response can't be handed to the next caller.
        if (!policy.Storable || (!policy.AllowAuthenticated && request.Headers.ContainsKey(HeaderNames.Authorization)))
        {
            var uncached = await next(request, cancellationToken);
            ApplyCacheHeaders(uncached, policy);
            return uncached;
        }

        var (noStore, noCache) = ReadRequestDirectives(request);
        if (noStore)
        {
            var uncached = await next(request, cancellationToken);
            ApplyCacheHeaders(uncached, policy);
            return uncached;
        }

        var key = BuildCacheKey(request, policy);

        // "no-cache" forces revalidation against the origin, but the fresh result may still be stored.
        if (!noCache && _cache.TryGet(key, out var entry))
            return BuildFromCachedEntry(request, entry);

        var response = await next(request, cancellationToken);
        ApplyCacheHeaders(response, policy);
        TryStore(key, response, policy);
        return response;
    }

    // ── Cache key ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Key layout: <c>method \n path[?query] \n generation \n vary-values</c>. The path generation
    /// makes invalidation O(1) — bumping it orphans every prior entry for the path.
    /// </summary>
    private string BuildCacheKey(HttpRequest request, CachePolicy policy)
    {
        var path = PathWithoutQuery(request.Path);
        var builder = new StringBuilder(request.Method.Length + request.Path.Length + 32);

        builder.Append(request.Method).Append('\n').Append(path);

        if (policy.VaryByQueryKeys != null)
        {
            // Only the declared keys select the representation, so unrelated query noise
            // (tracking parameters, key order) collapses onto the same entry.
            var query = request.Query;
            foreach (var queryKey in policy.VaryByQueryKeys)
            {
                builder.Append('\n').Append(queryKey).Append('=');
                if (query.TryGetValue(queryKey, out var value))
                    builder.Append(value);
            }
        }
        else
        {
            var queryIndex = request.Path.IndexOf('?');
            if (queryIndex >= 0)
                builder.Append(request.Path, queryIndex, request.Path.Length - queryIndex);
        }

        builder.Append('\n').Append(_cache.GetPathGeneration(path).ToString(CultureInfo.InvariantCulture));

        if (policy.VaryByHeaders != null)
        {
            foreach (var headerName in policy.VaryByHeaders)
            {
                builder.Append('\n');
                if (request.Headers.TryGetValue(headerName, out var value))
                    builder.Append(value);
            }
        }

        return builder.ToString();
    }

    private static string PathWithoutQuery(string path)
    {
        var queryIndex = path.IndexOf('?');
        return queryIndex >= 0 ? path[..queryIndex] : path;
    }

    // ── Serving a hit ──────────────────────────────────────────────────────────────────────────

    private static HttpResponse BuildFromCachedEntry(HttpRequest request, CachedResponse entry)
    {
        // Answer conditional requests here rather than leaving it to ApplyConditionalHeaders, which
        // only runs on the HTTP/1.1 connection loop — this way HTTP/2 and HTTP/3 revalidate too.
        if (request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch) &&
            EffinitiveServer.WeakETagMatch(ifNoneMatch, entry.ETag))
        {
            var notModified = new HttpResponse
            {
                StatusCode = 304,
                ContentType = entry.ContentType,
                Body = null
            };
            CopyCachedHeaders(entry, notModified);
            return notModified;
        }

        var response = new HttpResponse
        {
            StatusCode = entry.StatusCode,
            ContentType = entry.ContentType,
            // Shared by reference with every other request served from this entry — read-only.
            Body = entry.Body
        };
        CopyCachedHeaders(entry, response);
        return response;
    }

    private static void CopyCachedHeaders(CachedResponse entry, HttpResponse response)
    {
        for (int i = 0; i < entry.Headers.Length; i++)
            response.Headers[entry.Headers[i].Key] = entry.Headers[i].Value;

        response.Headers[HeaderNames.ETag] = entry.ETag;

        var ageSeconds = (DateTime.UtcNow.Ticks - entry.StoredAtTicks) / TimeSpan.TicksPerSecond;
        if (ageSeconds < 0)
            ageSeconds = 0;
        response.Headers[HeaderNames.Age] = ageSeconds.ToString(CultureInfo.InvariantCulture);
    }

    // ── Storing a miss ─────────────────────────────────────────────────────────────────────────

    private static void ApplyCacheHeaders(HttpResponse response, CachePolicy policy)
    {
        // An endpoint that set Cache-Control itself knows more than the attribute does.
        if (!response.Headers.ContainsKey(HeaderNames.CacheControl))
            response.Headers[HeaderNames.CacheControl] = policy.CacheControl;

        if (policy.VaryByHeaders != null)
        {
            foreach (var headerName in policy.VaryByHeaders)
                response.AppendVary(headerName);
        }
    }

    private void TryStore(string key, HttpResponse response, CachePolicy policy)
    {
        // No body to snapshot: SSE and other stream handlers own the connection, and stream-backed
        // bodies (static files) are consumed once by the writer.
        if (response.IsStreaming || response.BodyStream != null)
            return;

        // Compression is meant to run outside this middleware. If the flag is already set, the
        // ordering is wrong and the body we'd store may not match the headers — refuse rather than
        // cache something inconsistent.
        if (response.GzipCompressionLevel.HasValue)
            return;

        if (!IsCacheableStatus(response.StatusCode))
            return;

        var headers = response.HeadersOrNull;
        if (headers != null)
        {
            // A Set-Cookie is per-client by definition.
            if (headers.ContainsKey(HeaderNames.SetCookie))
                return;

            if (headers.TryGetValue(HeaderNames.CacheControl, out var cacheControl) &&
                (HasDirective(cacheControl, HeaderValues.NoStore) || HasDirective(cacheControl, "private")))
                return;
        }

        // Same trick the compression middleware uses — force deferred serialization so we can measure
        // and snapshot the actual bytes.
        response.MaterializeDeferredBody();

        var body = response.Body;
        if (body == null || body.Length > _options.MaxBodySizeBytes)
            return;

        // Setting the ETag here also means ApplyConditionalHeaders skips re-hashing this body on
        // every subsequent hit — it only computes one when the header is absent.
        var etag = EffinitiveServer.ComputeBodyETag(body);
        response.Headers[HeaderNames.ETag] = etag;

        var nowTicks = DateTime.UtcNow.Ticks;
        _cache.Set(key, new CachedResponse(
            response.StatusCode,
            response.ContentType,
            body,
            SnapshotHeaders(headers),
            etag,
            nowTicks,
            nowTicks + policy.Duration.Ticks));
    }

    /// <summary>
    /// Copies the headers worth replaying. Content-Type and ETag are stored on the entry itself;
    /// Content-Length, Connection, Date, Server and Age are regenerated per response by the writer or
    /// by <see cref="CopyCachedHeaders"/>, so replaying stale values would be wrong.
    /// </summary>
    private static KeyValuePair<string, string>[] SnapshotHeaders(Dictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0)
            return Array.Empty<KeyValuePair<string, string>>();

        var snapshot = new List<KeyValuePair<string, string>>(headers.Count);
        foreach (var header in headers)
        {
            if (IsPerResponseHeader(header.Key))
                continue;
            snapshot.Add(header);
        }

        return snapshot.Count == 0 ? Array.Empty<KeyValuePair<string, string>>() : snapshot.ToArray();
    }

    private static bool IsPerResponseHeader(string name) =>
        name.Equals(HeaderNames.ContentType, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(HeaderNames.Connection, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(HeaderNames.ETag, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(HeaderNames.Age, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(HeaderNames.Server, StringComparison.OrdinalIgnoreCase);

    private bool IsCacheableStatus(int statusCode)
    {
        var cacheable = _options.CacheableStatusCodes;
        for (int i = 0; i < cacheable.Length; i++)
        {
            if (cacheable[i] == statusCode)
                return true;
        }
        return false;
    }

    // ── Request inspection ─────────────────────────────────────────────────────────────────────

    private static bool IsCacheableMethod(string method) =>
        method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase) ||
        method.Equals(HttpMethods.Head, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsafeMethod(string method) =>
        method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase) ||
        method.Equals(HttpMethods.Put, StringComparison.OrdinalIgnoreCase) ||
        method.Equals(HttpMethods.Patch, StringComparison.OrdinalIgnoreCase) ||
        method.Equals(HttpMethods.Delete, StringComparison.OrdinalIgnoreCase);

    private static (bool NoStore, bool NoCache) ReadRequestDirectives(HttpRequest request)
    {
        bool noStore = false, noCache = false;

        if (request.Headers.TryGetValue(HeaderNames.CacheControl, out var cacheControl))
        {
            noStore = HasDirective(cacheControl, HeaderValues.NoStore);
            noCache = HasDirective(cacheControl, HeaderValues.NoCache);
        }

        // HTTP/1.0 clients express the same intent through Pragma.
        if (!noCache && request.Headers.TryGetValue(HeaderNames.Pragma, out var pragma))
            noCache = HasDirective(pragma, HeaderValues.NoCache);

        return (noStore, noCache);
    }

    /// <summary>
    /// Token-aware directive test: matches the name before any "=", so <c>no-cache="Set-Cookie"</c>
    /// counts while an unrelated extension directive that merely contains the text does not.
    /// </summary>
    private static bool HasDirective(string headerValue, string directive)
    {
        var remaining = headerValue.AsSpan();
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var token = (comma >= 0 ? remaining[..comma] : remaining).Trim();
            remaining = comma >= 0 ? remaining[(comma + 1)..] : ReadOnlySpan<char>.Empty;

            var equals = token.IndexOf('=');
            if (equals >= 0)
                token = token[..equals].Trim();

            if (token.Equals(directive, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Policy ─────────────────────────────────────────────────────────────────────────────────

    private CachePolicy? ResolvePolicy(HttpRequest request)
    {
        // Populated by the server before the pipeline runs, and only for endpoint-typed routes —
        // delegate-based routes have no type to hang an attribute on and are never cached.
        if (request.Items == null ||
            !request.Items.TryGetValue("EndpointType", out var endpointTypeObj) ||
            endpointTypeObj is not Type endpointType)
            return null;

        return _policies.GetOrAdd(endpointType, static (type, options) => CachePolicy.FromAttribute(type, options), _options);
    }

    /// <summary>
    /// The per-endpoint-type decisions, resolved once and reused: header strings are precomputed so a
    /// request only concatenates its cache key.
    /// </summary>
    private sealed class CachePolicy
    {
        public TimeSpan Duration { get; private init; }
        public string CacheControl { get; private init; } = HeaderValues.NoStore;
        public string[]? VaryByHeaders { get; private init; }
        public string[]? VaryByQueryKeys { get; private init; }
        public bool Storable { get; private init; }
        public bool AllowAuthenticated { get; private init; }

        public static CachePolicy? FromAttribute(Type endpointType, ResponseCacheOptions options)
        {
            var attribute = endpointType.GetCustomAttribute<ResponseCacheAttribute>(inherit: true);
            if (attribute == null)
                return null;

            var duration = attribute.Duration > 0
                ? TimeSpan.FromSeconds(attribute.Duration)
                : options.DefaultDuration;

            var seconds = (int)duration.TotalSeconds;
            var cacheControl = attribute.NoStore
                ? HeaderValues.NoStore
                : attribute.Location switch
                {
                    ResponseCacheLocation.None => $"{HeaderValues.NoCache}, {HeaderValues.NoStore}",
                    ResponseCacheLocation.Client => $"private, max-age={seconds.ToString(CultureInfo.InvariantCulture)}",
                    _ => $"public, max-age={seconds.ToString(CultureInfo.InvariantCulture)}"
                };

            return new CachePolicy
            {
                Duration = duration,
                CacheControl = cacheControl,
                VaryByHeaders = SplitList(attribute.VaryByHeader),
                VaryByQueryKeys = SplitList(attribute.VaryByQueryKeys),
                AllowAuthenticated = attribute.AllowAuthenticated,
                Storable = !attribute.NoStore
                           && attribute.Location == ResponseCacheLocation.Any
                           && duration > TimeSpan.Zero
            };
        }

        private static string[]? SplitList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? null : parts;
        }
    }
}
