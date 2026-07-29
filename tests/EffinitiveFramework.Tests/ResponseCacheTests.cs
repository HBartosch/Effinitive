using System.Text;
using EffinitiveFramework.Core.Caching;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for <see cref="ResponseCacheMiddleware"/> and <see cref="MemoryResponseCache"/>.
/// The middleware is exercised directly with a stand-in <see cref="RequestDelegate"/>, so no server
/// or socket is involved — the delegate's invocation count is what proves a hit skipped the endpoint.
/// </summary>
public class ResponseCacheTests
{
    // ── Endpoint stubs: only their attributes matter, the middleware never instantiates them ──

    [ResponseCache(Duration = 60)]
    private sealed class CachedEndpoint { }

    /// <summary>No Duration — falls back to <see cref="ResponseCacheOptions.DefaultDuration"/>.</summary>
    [ResponseCache]
    private sealed class DefaultDurationEndpoint { }

    private sealed class UncachedEndpoint { }

    [ResponseCache(Duration = 60, VaryByHeader = "Accept-Language")]
    private sealed class VaryByHeaderEndpoint { }

    [ResponseCache(Duration = 60, VaryByQueryKeys = "page")]
    private sealed class VaryByQueryEndpoint { }

    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    private sealed class ClientOnlyEndpoint { }

    [ResponseCache(Duration = 60, NoStore = true)]
    private sealed class NoStoreEndpoint { }

    [ResponseCache(Duration = 60, AllowAuthenticated = true)]
    private sealed class PublicAuthenticatedEndpoint { }

    // ── Helpers ──

    private static HttpRequest Request(string method, string path, Type? endpointType, params string[] headerPairs)
    {
        var request = new HttpRequest { Method = method, Path = path };

        if (endpointType != null)
            request.Items = new Dictionary<string, object> { ["EndpointType"] = endpointType };

        for (int i = 0; i + 1 < headerPairs.Length; i += 2)
            request.Headers[headerPairs[i]] = headerPairs[i + 1];

        return request;
    }

    /// <summary>A next-delegate that returns a distinct body per call and counts invocations.</summary>
    private static RequestDelegate CountingNext(Counter counter, int statusCode = 200)
        => (_, _) =>
        {
            counter.Calls++;
            return new ValueTask<HttpResponse>(new HttpResponse
            {
                StatusCode = statusCode,
                ContentType = MediaTypes.ApplicationJson,
                Body = Encoding.UTF8.GetBytes($"{{\"call\":{counter.Calls}}}")
            });
        };

    private sealed class Counter { public int Calls; }

    private static string BodyText(HttpResponse response) =>
        response.Body == null ? string.Empty : Encoding.UTF8.GetString(response.Body);

    // ── Basic hit / miss ──

    [Fact]
    public async Task SecondRequest_IsServedFromCache_WithoutInvokingEndpoint()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        var first = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        var second = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal(1, counter.Calls);
        Assert.Equal(BodyText(first), BodyText(second));
        Assert.Equal("public, max-age=60", second.Headers[HeaderNames.CacheControl]);
        Assert.True(second.Headers.ContainsKey(HeaderNames.Age));
        Assert.False(first.Headers.ContainsKey(HeaderNames.Age));
        Assert.Equal(1L, middleware.Cache.Hits);
        Assert.Equal(1L, middleware.Cache.Misses);
    }

    [Fact]
    public async Task EndpointWithoutAttribute_IsNeverCached()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(UncachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(UncachedEndpoint)), next, default);

        Assert.Equal(2, counter.Calls);
        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task RouteWithoutEndpointType_IsNeverCached()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", endpointType: null), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items", endpointType: null), next, default);

        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task DifferentPaths_DoNotShareAnEntry()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/a", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/b", typeof(CachedEndpoint)), next, default);

        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task Entry_ExpiresAfterItsDuration()
    {
        var middleware = new ResponseCacheMiddleware(new ResponseCacheOptions
        {
            DefaultDuration = TimeSpan.FromMilliseconds(50)
        });
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(DefaultDurationEndpoint)), next, default);
        await Task.Delay(150);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(DefaultDurationEndpoint)), next, default);

        Assert.Equal(2, counter.Calls);
    }

    // ── Vary ──

    [Fact]
    public async Task VaryByHeader_SelectsSeparateEntries_AndIsAdvertised()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        var en = await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(VaryByHeaderEndpoint), "Accept-Language", "en"), next, default);
        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(VaryByHeaderEndpoint), "Accept-Language", "fr"), next, default);
        var enAgain = await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(VaryByHeaderEndpoint), "Accept-Language", "en"), next, default);

        Assert.Equal(2, counter.Calls);
        Assert.Equal(BodyText(en), BodyText(enAgain));
        Assert.Equal("Accept-Language", en.Headers[HeaderNames.Vary]);
    }

    [Fact]
    public async Task VaryByQueryKeys_IgnoresUndeclaredQueryParameters()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        // Same "page", different tracking parameter — one entry.
        await middleware.InvokeAsync(Request("GET", "/api/items?page=1&utm=a", typeof(VaryByQueryEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items?page=1&utm=b", typeof(VaryByQueryEndpoint)), next, default);
        Assert.Equal(1, counter.Calls);

        // Different "page" — separate entry.
        await middleware.InvokeAsync(Request("GET", "/api/items?page=2", typeof(VaryByQueryEndpoint)), next, default);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task WithoutVaryByQueryKeys_TheWholeQueryStringSelectsTheEntry()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items?page=1", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items?page=2", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items?page=1", typeof(CachedEndpoint)), next, default);

        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task VaryFromPolicy_SurvivesCompressionAppendingAcceptEncoding()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();

        var response = await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(VaryByHeaderEndpoint), "Accept-Language", "en"),
            CountingNext(counter), default);

        // What ResponseCompressionMiddleware does once the response comes back out through it.
        response.AppendVary(HeaderNames.AcceptEncoding);

        Assert.Equal("Accept-Language, Accept-Encoding", response.Headers[HeaderNames.Vary]);
    }

    [Fact]
    public async Task CacheHit_IsStillCompressed_WhenRegisteredInsideCompression()
    {
        // The documented ordering: compression outermost, caching inside it. A hit returns out
        // through the compression middleware, so it is compressed exactly like a miss — and the
        // stored body stays uncompressed, which is what makes that possible.
        var pipeline = new MiddlewarePipeline();
        pipeline.Use(new ResponseCompressionMiddleware(System.IO.Compression.CompressionLevel.Fastest, minimumSize: 64));
        pipeline.Use(new ResponseCacheMiddleware());

        var counter = new Counter();
        RequestDelegate endpoint = (_, _) =>
        {
            counter.Calls++;
            return new ValueTask<HttpResponse>(new HttpResponse
            {
                StatusCode = 200,
                ContentType = MediaTypes.ApplicationJson,
                Body = Encoding.UTF8.GetBytes(new string('x', 512))
            });
        };

        await pipeline.ExecuteAsync(
            Request("GET", "/api/big", typeof(CachedEndpoint), HeaderNames.AcceptEncoding, "gzip"), endpoint, default);
        var hit = await pipeline.ExecuteAsync(
            Request("GET", "/api/big", typeof(CachedEndpoint), HeaderNames.AcceptEncoding, "gzip"), endpoint, default);

        Assert.Equal(1, counter.Calls);
        Assert.NotNull(hit.GzipCompressionLevel);
        Assert.Equal(HeaderValues.Gzip, hit.Headers[HeaderNames.ContentEncoding]);
        Assert.Equal(HeaderNames.AcceptEncoding, hit.Headers[HeaderNames.Vary]);
        Assert.True(hit.Headers.ContainsKey(HeaderNames.Age));
    }

    // ── Conditional requests ──

    [Fact]
    public async Task CacheHit_WithMatchingIfNoneMatch_Returns304WithoutBody()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        var stored = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        var etag = stored.Headers[HeaderNames.ETag];

        var revalidated = await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.IfNoneMatch, etag), next, default);

        Assert.Equal(1, counter.Calls);
        Assert.Equal(304, revalidated.StatusCode);
        Assert.Null(revalidated.Body);
        Assert.Equal(etag, revalidated.Headers[HeaderNames.ETag]);
    }

    [Fact]
    public async Task CacheHit_WithNonMatchingIfNoneMatch_ReturnsFullBody()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        var stored = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        var served = await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.IfNoneMatch, "\"stale\""), next, default);

        Assert.Equal(200, served.StatusCode);
        Assert.Equal(BodyText(stored), BodyText(served));
    }

    // ── Invalidation ──

    [Fact]
    public async Task SuccessfulWrite_InvalidatesTheCachedRepresentationsOfThatPath()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("POST", "/api/items", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        // GET, POST, then a re-executed GET.
        Assert.Equal(3, counter.Calls);
    }

    [Fact]
    public async Task FailedWrite_DoesNotInvalidate()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), CountingNext(counter), default);
        await middleware.InvokeAsync(Request("POST", "/api/items", typeof(CachedEndpoint)), CountingNext(counter, statusCode: 400), default);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), CountingNext(counter), default);

        // The final GET still comes from cache, so only the GET and the failed POST ran.
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task WriteToAnotherPath_DoesNotInvalidate()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("POST", "/api/other", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal(2, counter.Calls);
    }

    // ── Requests that must not be served from the shared store ──

    [Fact]
    public async Task AuthorizationHeader_BypassesTheSharedCacheByDefault()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.Authorization, "Bearer alice"), next, default);
        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.Authorization, "Bearer bob"), next, default);

        Assert.Equal(2, counter.Calls);
        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task AuthorizationHeader_IsCachedWhenTheEndpointOptsIn()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(PublicAuthenticatedEndpoint), HeaderNames.Authorization, "Bearer alice"), next, default);
        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(PublicAuthenticatedEndpoint), HeaderNames.Authorization, "Bearer bob"), next, default);

        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task RequestNoStore_NeitherReadsNorWritesTheCache()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.CacheControl, "no-store"), next, default);
        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.CacheControl, "no-store"), next, default);

        Assert.Equal(2, counter.Calls);
        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task RequestNoCache_RevalidatesButStillStores()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        // no-cache forces a fresh response...
        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.CacheControl, "no-cache"), next, default);
        Assert.Equal(2, counter.Calls);

        // ...which replaced the stored entry, so an ordinary request is a hit again.
        var third = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        Assert.Equal(2, counter.Calls);
        Assert.Equal("{\"call\":2}", BodyText(third));
    }

    [Fact]
    public async Task PragmaNoCache_IsHonored()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(
            Request("GET", "/api/items", typeof(CachedEndpoint), HeaderNames.Pragma, "no-cache"), next, default);

        Assert.Equal(2, counter.Calls);
    }

    // ── Responses that must not be stored ──

    [Fact]
    public async Task ClientLocation_EmitsPrivateHeaderAndStoresNothing()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        var response = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(ClientOnlyEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(ClientOnlyEndpoint)), next, default);

        Assert.Equal("private, max-age=60", response.Headers[HeaderNames.CacheControl]);
        Assert.Equal(2, counter.Calls);
        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task NoStorePolicy_EmitsNoStoreAndStoresNothing()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        var response = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(NoStoreEndpoint)), next, default);
        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(NoStoreEndpoint)), next, default);

        Assert.Equal("no-store", response.Headers[HeaderNames.CacheControl]);
        Assert.Equal(2, counter.Calls);
        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task NonCacheableStatusCode_IsNotStored()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), CountingNext(counter, statusCode: 500), default);

        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task StreamingResponse_IsNotStored()
    {
        var middleware = new ResponseCacheMiddleware();

        RequestDelegate next = (_, _) => new ValueTask<HttpResponse>(new HttpResponse
        {
            StatusCode = 200,
            ContentType = MediaTypes.TextEventStream,
            StreamHandler = (_, _) => Task.CompletedTask
        });

        await middleware.InvokeAsync(Request("GET", "/api/stream", typeof(CachedEndpoint)), next, default);

        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task ResponseWithSetCookie_IsNotStored()
    {
        var middleware = new ResponseCacheMiddleware();

        RequestDelegate next = (_, _) =>
        {
            var response = new HttpResponse { StatusCode = 200, Body = Encoding.UTF8.GetBytes("{}") };
            response.Headers[HeaderNames.SetCookie] = "session=abc";
            return new ValueTask<HttpResponse>(response);
        };

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task EndpointsOwnCacheControl_WinsOverThePolicyAndCanSuppressStorage()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();

        RequestDelegate next = (_, _) =>
        {
            counter.Calls++;
            var response = new HttpResponse { StatusCode = 200, Body = Encoding.UTF8.GetBytes("{}") };
            response.Headers[HeaderNames.CacheControl] = "no-store";
            return new ValueTask<HttpResponse>(response);
        };

        var response = await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal("no-store", response.Headers[HeaderNames.CacheControl]);
        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task BodyLargerThanMaxBodySize_IsNotStored()
    {
        var middleware = new ResponseCacheMiddleware(new ResponseCacheOptions { MaxBodySizeBytes = 16 });

        RequestDelegate next = (_, _) => new ValueTask<HttpResponse>(new HttpResponse
        {
            StatusCode = 200,
            Body = new byte[64]
        });

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    [Fact]
    public async Task AlreadyCompressedResponse_IsNotStored()
    {
        // Guards the ordering rule: if compression ran inside the cache, the body and headers we'd
        // snapshot would disagree, so the middleware refuses to store it.
        var middleware = new ResponseCacheMiddleware();

        RequestDelegate next = (_, _) => new ValueTask<HttpResponse>(new HttpResponse
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("{}"),
            GzipCompressionLevel = System.IO.Compression.CompressionLevel.Fastest
        });

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal(0, middleware.Cache.EntryCount);
    }

    // ── HEAD ──

    [Fact]
    public async Task HeadAndGet_UseSeparateEntries()
    {
        var middleware = new ResponseCacheMiddleware();
        var counter = new Counter();
        var next = CountingNext(counter);

        await middleware.InvokeAsync(Request("GET", "/api/items", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("HEAD", "/api/items", typeof(CachedEndpoint)), next, default);
        await middleware.InvokeAsync(Request("HEAD", "/api/items", typeof(CachedEndpoint)), next, default);

        Assert.Equal(2, counter.Calls);
    }

    // ── Store ──

    [Fact]
    public void ConfiguredStore_IsUsedAndExposedForStatistics()
    {
        var options = new ResponseCacheOptions();
        var middleware = new ResponseCacheMiddleware(options);

        Assert.NotNull(options.Store);
        Assert.Same(options.Store, middleware.Cache);
    }

    [Fact]
    public void MemoryCache_EvictsOldestEntriesWhenOverBudget()
    {
        // Budget fits roughly two 1 KB entries once per-entry overhead is counted. Stored-at times are
        // staggered explicitly — the system clock's granularity is coarser than three back-to-back
        // Set() calls, so real timestamps would tie and make eviction order arbitrary.
        var cache = new MemoryResponseCache(maxSizeBytes: 2500);

        cache.Set("a", Entry(new byte[1024], ageSeconds: 30));
        cache.Set("b", Entry(new byte[1024], ageSeconds: 20));
        Assert.Equal(2, cache.EntryCount);

        cache.Set("c", Entry(new byte[1024], ageSeconds: 10));

        Assert.True(cache.SizeBytes <= 2500);
        Assert.True(cache.EntryCount < 3);
        Assert.True(cache.TryGet("c", out _));       // newest survives
        Assert.False(cache.TryGet("a", out _));      // oldest evicted first
    }

    [Fact]
    public void MemoryCache_RejectsAnEntryLargerThanTheWholeBudget()
    {
        var cache = new MemoryResponseCache(maxSizeBytes: 512);

        cache.Set("huge", Entry(new byte[4096]));

        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(0L, cache.SizeBytes);
    }

    [Fact]
    public void MemoryCache_InvalidatePathBumpsTheGeneration()
    {
        var cache = new MemoryResponseCache();

        Assert.Equal(0L, cache.GetPathGeneration("/api/items"));

        cache.InvalidatePath("/api/items");
        var first = cache.GetPathGeneration("/api/items");
        Assert.NotEqual(0L, first);

        cache.InvalidatePath("/api/items");
        Assert.NotEqual(first, cache.GetPathGeneration("/api/items"));
        Assert.Equal(0L, cache.GetPathGeneration("/api/other"));
    }

    [Fact]
    public void MemoryCache_ExpiredEntryIsReportedAsAMissAndReclaimed()
    {
        var cache = new MemoryResponseCache();
        var expired = new CachedResponse(
            200, MediaTypes.ApplicationJson, new byte[128], Array.Empty<KeyValuePair<string, string>>(),
            "\"tag\"", DateTime.UtcNow.Ticks - TimeSpan.TicksPerMinute, DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond);

        cache.Set("stale", expired);

        Assert.False(cache.TryGet("stale", out _));
        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(0L, cache.SizeBytes);
        Assert.Equal(1L, cache.Misses);
    }

    private static CachedResponse Entry(byte[] body, int ageSeconds = 0)
    {
        var storedAt = DateTime.UtcNow.Ticks - (ageSeconds * TimeSpan.TicksPerSecond);
        return new CachedResponse(
            200, MediaTypes.ApplicationJson, body, Array.Empty<KeyValuePair<string, string>>(),
            "\"tag\"", storedAt, DateTime.UtcNow.Ticks + TimeSpan.TicksPerHour);
    }
}
