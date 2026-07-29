using System.Net;
using System.Text;
using System.Text.Json;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.RateLimiting;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for token-bucket rate limiting. The bucket and store take the current tick count as a
/// parameter, so time-dependent behaviour is asserted deterministically rather than with sleeps.
/// </summary>
public class RateLimitingTests
{
    private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    private static RateLimitPolicy Policy(int permits = 5, int windowSeconds = 10, string name = "test")
        => new(permits, TimeSpan.FromSeconds(windowSeconds), name);

    private static HttpRequest Request(string? ip, params string[] headerPairs)
    {
        var request = new HttpRequest
        {
            Method = "GET",
            Path = "/api/test",
            RemoteIpAddress = ip == null ? null : IPAddress.Parse(ip)
        };

        for (int i = 0; i + 1 < headerPairs.Length; i += 2)
            request.Headers[headerPairs[i]] = headerPairs[i + 1];

        return request;
    }

    // ── Policy ──

    [Fact]
    public void Policy_DerivesRefillRateFromLimitAndWindow()
    {
        var policy = Policy(permits: 60, windowSeconds: 60);
        Assert.Equal(1.0, policy.TokensPerSecond, 3);
    }

    [Fact]
    public void Policy_RejectsNonPositiveConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitPolicy(1, TimeSpan.Zero));
    }

    [Fact]
    public void Policy_RetryAfterIsAlwaysAtLeastOneSecondWhenEmpty()
    {
        // A fast refill would otherwise round down to 0, telling clients to retry immediately.
        var policy = Policy(permits: 1000, windowSeconds: 1);
        Assert.Equal(1, policy.SecondsUntilNextToken(0));
    }

    // ── Bucket ──

    [Fact]
    public void Bucket_AllowsBurstUpToCapacityThenDenies()
    {
        var policy = Policy(permits: 3, windowSeconds: 60);
        var bucket = new TokenBucket(policy, 0);

        for (int i = 0; i < 3; i++)
            Assert.True(bucket.TryAcquire(0, out _, out _), $"request {i + 1} should be allowed");

        Assert.False(bucket.TryAcquire(0, out var retryAfter, out var remaining));
        Assert.True(retryAfter >= 1);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void Bucket_ReportsRemainingPermits()
    {
        var bucket = new TokenBucket(Policy(permits: 3, windowSeconds: 60), 0);

        bucket.TryAcquire(0, out _, out var afterFirst);
        Assert.Equal(2, afterFirst);

        bucket.TryAcquire(0, out _, out var afterSecond);
        Assert.Equal(1, afterSecond);
    }

    [Fact]
    public void Bucket_RefillsOverTime()
    {
        // 10 permits per 10s = 1 per second.
        var bucket = new TokenBucket(Policy(permits: 10, windowSeconds: 10), 0);

        for (int i = 0; i < 10; i++)
            bucket.TryAcquire(0, out _, out _);
        Assert.False(bucket.TryAcquire(0, out _, out _));

        // Two seconds later, two permits are back.
        Assert.True(bucket.TryAcquire(Ticks(2), out _, out _));
        Assert.True(bucket.TryAcquire(Ticks(2), out _, out _));
        Assert.False(bucket.TryAcquire(Ticks(2), out _, out _));
    }

    [Fact]
    public void Bucket_RefillIsCappedAtCapacity()
    {
        var bucket = new TokenBucket(Policy(permits: 3, windowSeconds: 10), 0);

        for (int i = 0; i < 3; i++)
            bucket.TryAcquire(0, out _, out _);

        // An hour of idling must not bank more than the bucket holds.
        for (int i = 0; i < 3; i++)
            Assert.True(bucket.TryAcquire(Ticks(3600), out _, out _));

        Assert.False(bucket.TryAcquire(Ticks(3600), out _, out _));
    }

    [Fact]
    public void Bucket_ClockGoingBackwardsDoesNotConsumePermits()
    {
        // An NTP correction must not subtract tokens via a negative elapsed time.
        var bucket = new TokenBucket(Policy(permits: 2, windowSeconds: 10), Ticks(100));

        Assert.True(bucket.TryAcquire(Ticks(50), out _, out _));
        Assert.True(bucket.TryAcquire(Ticks(50), out _, out _));
        Assert.False(bucket.TryAcquire(Ticks(50), out _, out _));
    }

    [Fact]
    public void Bucket_BecomesDisposableOnceIdleForAWindow()
    {
        var bucket = new TokenBucket(Policy(permits: 5, windowSeconds: 10), 0);
        bucket.TryAcquire(0, out _, out _);

        Assert.False(bucket.IsDisposableAt(Ticks(5)));
        Assert.True(bucket.IsDisposableAt(Ticks(11)));
    }

    // ── Store ──

    [Fact]
    public void Store_TracksPartitionsIndependently()
    {
        var store = new MemoryRateLimitStore();
        var policy = Policy(permits: 1, windowSeconds: 60);

        Assert.True(store.TryAcquire("10.0.0.1", policy, 0, out _, out _));
        Assert.False(store.TryAcquire("10.0.0.1", policy, 0, out _, out _));

        // A different client is unaffected by the first one's spend.
        Assert.True(store.TryAcquire("10.0.0.2", policy, 0, out _, out _));
        Assert.Equal(2, store.TrackedClients);
    }

    [Fact]
    public void Store_KeepsPoliciesSeparateForTheSameClient()
    {
        var store = new MemoryRateLimitStore();
        var global = Policy(permits: 1, windowSeconds: 60, name: "global");
        var endpoint = Policy(permits: 1, windowSeconds: 60, name: "endpoint");

        Assert.True(store.TryAcquire("10.0.0.1", global, 0, out _, out _));
        // Same client, different policy: its own allowance, not the global one already spent.
        Assert.True(store.TryAcquire("10.0.0.1", endpoint, 0, out _, out _));
        Assert.Equal(2, store.TrackedClients);
    }

    [Fact]
    public void Store_EvictsIdleClientsWhenOverCap()
    {
        var store = new MemoryRateLimitStore(maxTrackedClients: 10);
        var policy = Policy(permits: 5, windowSeconds: 1);

        for (int i = 0; i < 10; i++)
            store.TryAcquire($"10.0.0.{i}", policy, 0, out _, out _);
        Assert.Equal(10, store.TrackedClients);

        // A new client arriving well after the window sweeps the fully-refilled ones away.
        store.TryAcquire("10.0.1.1", policy, Ticks(60), out _, out _);
        Assert.True(store.TrackedClients < 10);
    }

    [Fact]
    public void Store_StaysBoundedWhenEveryClientIsActive()
    {
        // The adversarial case: many distinct addresses inside one window, none idle enough to expire.
        var store = new MemoryRateLimitStore(maxTrackedClients: 50);
        var policy = Policy(permits: 5, windowSeconds: 3600);

        for (int i = 0; i < 500; i++)
            store.TryAcquire($"10.0.{i / 256}.{i % 256}", policy, 0, out _, out _);

        Assert.True(store.TrackedClients <= 50, $"expected <= 50 tracked clients, got {store.TrackedClients}");
    }

    [Fact]
    public void Store_RejectsNonPositiveCap()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryRateLimitStore(0));

    [Fact]
    public void Store_ClearDropsEverything()
    {
        var store = new MemoryRateLimitStore();
        store.TryAcquire("10.0.0.1", Policy(), 0, out _, out _);

        store.Clear();

        Assert.Equal(0, store.TrackedClients);
    }

    // ── Partition resolution ──

    private static RateLimiter Limiter(Action<RateLimitOptions>? configure = null)
    {
        var options = new RateLimitOptions();
        configure?.Invoke(options);
        return new RateLimiter(options, new JsonSerializerOptions());
    }

    [Fact]
    public void Partition_UsesTheSocketAddressByDefault()
    {
        var limiter = Limiter();
        Assert.Equal("203.0.113.5", limiter.ResolvePartitionKey(Request("203.0.113.5")));
    }

    [Fact]
    public void Partition_IgnoresForwardedHeaderByDefault()
    {
        // The spoofing case: without opt-in, a caller cannot mint a fresh allowance with a header.
        var limiter = Limiter();
        var request = Request("203.0.113.5", HeaderNames.XForwardedFor, "1.2.3.4");

        Assert.Equal("203.0.113.5", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Partition_IgnoresForwardedHeaderFromAnUntrustedPeer()
    {
        // Forwarding is enabled, but this caller is not the configured proxy, so it is not believed.
        var limiter = Limiter(o => o.AddTrustedProxy("10.0.0.1"));
        var request = Request("203.0.113.5", HeaderNames.XForwardedFor, "1.2.3.4");

        Assert.Equal("203.0.113.5", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Partition_UsesForwardedHeaderFromATrustedProxy()
    {
        var limiter = Limiter(o => o.AddTrustedProxy("10.0.0.1"));
        var request = Request("10.0.0.1", HeaderNames.XForwardedFor, "203.0.113.9");

        Assert.Equal("203.0.113.9", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Partition_WalksForwardedChainRightToLeftSkippingTrustedHops()
    {
        var limiter = Limiter(o =>
        {
            o.AddTrustedProxy("10.0.0.1");
            o.AddTrustedProxy("10.0.0.2");
        });

        // Client, then two of our own proxies. The rightmost non-proxy entry is the real client.
        var request = Request("10.0.0.1", HeaderNames.XForwardedFor, "203.0.113.9, 10.0.0.2, 10.0.0.1");

        Assert.Equal("203.0.113.9", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Partition_ForwardedHeaderIgnoredWhenNoProxiesAreConfigured()
    {
        // Enabling the flag without naming a proxy is a misconfiguration, not permission to trust all.
        var limiter = Limiter(o => o.TrustForwardedHeaders = true);
        var request = Request("203.0.113.5", HeaderNames.XForwardedFor, "1.2.3.4");

        Assert.Equal("203.0.113.5", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Partition_StripsPortsFromForwardedEntries()
    {
        var limiter = Limiter(o => o.AddTrustedProxy("10.0.0.1"));

        Assert.Equal("203.0.113.9",
            limiter.ResolvePartitionKey(Request("10.0.0.1", HeaderNames.XForwardedFor, "203.0.113.9:51234")));

        Assert.Equal("2001:db8::1",
            limiter.ResolvePartitionKey(Request("10.0.0.1", HeaderNames.XForwardedFor, "[2001:db8::1]:443")));
    }

    [Fact]
    public void Partition_BareIpv6ForwardedEntryIsNotMistakenForHostPort()
    {
        var limiter = Limiter(o => o.AddTrustedProxy("10.0.0.1"));
        var request = Request("10.0.0.1", HeaderNames.XForwardedFor, "2001:db8::1");

        Assert.Equal("2001:db8::1", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Partition_MissingAddressFallsBackWithoutThrowing()
    {
        var limiter = Limiter();
        Assert.Equal("unknown", limiter.ResolvePartitionKey(Request(null)));
    }

    // ── Rejection ──

    [Fact]
    public void Global_AllowsUpToTheLimitThenRejects()
    {
        var limiter = Limiter(o => { o.PermitLimit = 3; o.Window = TimeSpan.FromMinutes(10); });
        var request = Request("203.0.113.5");

        for (int i = 0; i < 3; i++)
            Assert.False(limiter.TryRejectGlobal(request, new HttpResponse()), $"request {i + 1} should pass");

        Assert.True(limiter.TryRejectGlobal(request, new HttpResponse()));
    }

    [Fact]
    public void Global_LimitsEachClientSeparately()
    {
        var limiter = Limiter(o => { o.PermitLimit = 1; o.Window = TimeSpan.FromMinutes(10); });

        Assert.False(limiter.TryRejectGlobal(Request("203.0.113.5"), new HttpResponse()));
        Assert.True(limiter.TryRejectGlobal(Request("203.0.113.5"), new HttpResponse()));

        // A different client still has its full allowance.
        Assert.False(limiter.TryRejectGlobal(Request("203.0.113.6"), new HttpResponse()));
    }

    [Fact]
    public void Rejection_Writes429WithRetryAfterAndProblemDetails()
    {
        var limiter = Limiter(o => { o.PermitLimit = 1; o.Window = TimeSpan.FromMinutes(10); });
        var request = Request("203.0.113.5");
        limiter.TryRejectGlobal(request, new HttpResponse());

        var response = new HttpResponse();
        Assert.True(limiter.TryRejectGlobal(request, response));

        Assert.Equal(429, response.StatusCode);
        Assert.Equal(MediaTypes.ApplicationProblemJson, response.ContentType);
        Assert.True(int.Parse(response.Headers[HeaderNames.RetryAfter]) >= 1);

        var body = Encoding.UTF8.GetString(response.Body!);
        Assert.Contains("Too Many Requests", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejection_EmitsRateLimitHeaders()
    {
        var limiter = Limiter(o => { o.PermitLimit = 1; o.Window = TimeSpan.FromMinutes(10); });
        var request = Request("203.0.113.5");
        limiter.TryRejectGlobal(request, new HttpResponse());

        var response = new HttpResponse();
        limiter.TryRejectGlobal(request, response);

        Assert.Equal("1", response.Headers[HeaderNames.XRateLimitLimit]);
        Assert.Equal("0", response.Headers[HeaderNames.XRateLimitRemaining]);
        Assert.True(response.Headers.ContainsKey(HeaderNames.XRateLimitReset));
    }

    [Fact]
    public void Rejection_RateLimitHeadersCanBeSuppressed()
    {
        var limiter = Limiter(o =>
        {
            o.PermitLimit = 1;
            o.Window = TimeSpan.FromMinutes(10);
            o.EmitRateLimitHeaders = false;
        });
        var request = Request("203.0.113.5");
        limiter.TryRejectGlobal(request, new HttpResponse());

        var response = new HttpResponse();
        limiter.TryRejectGlobal(request, response);

        Assert.False(response.Headers.ContainsKey(HeaderNames.XRateLimitLimit));
        // Retry-After is standard and stays regardless.
        Assert.True(response.Headers.ContainsKey(HeaderNames.RetryAfter));
    }

    [Fact]
    public void Rejection_HonorsCustomStatusCode()
    {
        var limiter = Limiter(o =>
        {
            o.PermitLimit = 1;
            o.Window = TimeSpan.FromMinutes(10);
            o.RejectionStatusCode = 503;
        });
        var request = Request("203.0.113.5");
        limiter.TryRejectGlobal(request, new HttpResponse());

        var response = new HttpResponse();
        limiter.TryRejectGlobal(request, response);

        Assert.Equal(503, response.StatusCode);
    }

    // ── Endpoint policies ──

    [RateLimit(PermitLimit = 2, WindowSeconds = 600)]
    private sealed class TightlyLimitedEndpoint { }

    [DisableRateLimit]
    private sealed class ExemptEndpoint { }

    private sealed class OrdinaryEndpoint { }

    [RateLimit(PermitLimit = 0, WindowSeconds = 0)]
    private sealed class MisconfiguredEndpoint { }

    [Fact]
    public void Endpoint_AppliesItsOwnTighterLimit()
    {
        var limiter = Limiter(o => { o.PermitLimit = 1000; o.Window = TimeSpan.FromMinutes(10); });
        var request = Request("203.0.113.5");

        Assert.False(limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(TightlyLimitedEndpoint)));
        Assert.False(limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(TightlyLimitedEndpoint)));
        Assert.True(limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(TightlyLimitedEndpoint)));
    }

    [Fact]
    public void Endpoint_WithoutAttributesHasNoOwnLimit()
    {
        var limiter = Limiter();
        var request = Request("203.0.113.5");

        for (int i = 0; i < 50; i++)
            Assert.False(limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(OrdinaryEndpoint)));

        Assert.False(limiter.IsExempt(typeof(OrdinaryEndpoint)));
    }

    [Fact]
    public void Endpoint_DisableRateLimitMarksItExempt()
    {
        var limiter = Limiter();

        Assert.True(limiter.IsExempt(typeof(ExemptEndpoint)));
        Assert.False(limiter.TryRejectEndpoint(Request("203.0.113.5"), new HttpResponse(), typeof(ExemptEndpoint)));
    }

    [Fact]
    public void Endpoint_MisconfiguredAttributeIsIgnoredRatherThanThrowing()
    {
        var limiter = Limiter();
        var request = Request("203.0.113.5");

        // A zero permit limit would throw if handed to RateLimitPolicy; it must degrade to "no policy".
        Assert.False(limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(MisconfiguredEndpoint)));
    }

    [Fact]
    public void Endpoint_LimitIsSeparateFromTheGlobalAllowance()
    {
        var limiter = Limiter(o => { o.PermitLimit = 100; o.Window = TimeSpan.FromMinutes(10); });
        var request = Request("203.0.113.5");

        // Exhaust the endpoint's own allowance.
        limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(TightlyLimitedEndpoint));
        limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(TightlyLimitedEndpoint));
        Assert.True(limiter.TryRejectEndpoint(request, new HttpResponse(), typeof(TightlyLimitedEndpoint)));

        // The global allowance is untouched, so other endpoints still work.
        Assert.False(limiter.TryRejectGlobal(request, new HttpResponse()));
    }

    [Fact]
    public void Limiter_ExposesItsStoreForDiagnostics()
    {
        var options = new RateLimitOptions();
        var limiter = new RateLimiter(options, new JsonSerializerOptions());

        limiter.TryRejectGlobal(Request("203.0.113.5"), new HttpResponse());

        Assert.NotNull(options.Store);
        Assert.Same(options.Store, limiter.Store);
        Assert.Equal(1, limiter.Store.TrackedClients);
    }

    // ── 429 plumbing ──

    [Fact]
    public void Response_KnowsTheReasonPhraseFor429()
        => Assert.Equal("Too Many Requests", new HttpResponse { StatusCode = 429 }.GetStatusText());

    // ── HTTP/2 and HTTP/3 plumbing ──
    //
    // These protocols build their requests through Http2RequestConverter rather than HttpConnection,
    // so the client address reaches them by a different route than HTTP/1.1 and needs its own coverage.

    private static List<(string name, string value)> Http2Headers() => new()
    {
        (":method", "GET"),
        (":path", "/api/test"),
        (":scheme", "https"),
        (":authority", "localhost")
    };

    [Fact]
    public void Http2Converter_StampsTheClientAddressOntoTheRequest()
    {
        var request = EffinitiveFramework.Core.Http2.Http2RequestConverter.ConvertToHttp1Request(
            Http2Headers(), Array.Empty<byte>(), IPAddress.Parse("203.0.113.7"));

        Assert.Equal("203.0.113.7", request.RemoteIpAddress?.ToString());
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/test", request.Path);
    }

    [Fact]
    public void Http2Converter_WithoutAnAddressLeavesItNull()
    {
        var request = EffinitiveFramework.Core.Http2.Http2RequestConverter.ConvertToHttp1Request(
            Http2Headers(), Array.Empty<byte>());

        Assert.Null(request.RemoteIpAddress);
    }

    [Fact]
    public void Http2Request_PartitionsByClientAddress()
    {
        // The converter does not populate the cached string form (that is a per-connection value the
        // protocol connections stamp on afterwards), so this also covers the ToString fallback.
        var limiter = Limiter();

        var request = EffinitiveFramework.Core.Http2.Http2RequestConverter.ConvertToHttp1Request(
            Http2Headers(), Array.Empty<byte>(), IPAddress.Parse("203.0.113.7"));

        Assert.Equal("203.0.113.7", limiter.ResolvePartitionKey(request));
    }

    [Fact]
    public void Http2Request_IsLimitedPerClientLikeHttp1()
    {
        var limiter = Limiter(o => { o.PermitLimit = 1; o.Window = TimeSpan.FromMinutes(10); });

        var first = EffinitiveFramework.Core.Http2.Http2RequestConverter.ConvertToHttp1Request(
            Http2Headers(), Array.Empty<byte>(), IPAddress.Parse("203.0.113.7"));
        var second = EffinitiveFramework.Core.Http2.Http2RequestConverter.ConvertToHttp1Request(
            Http2Headers(), Array.Empty<byte>(), IPAddress.Parse("203.0.113.7"));
        var otherClient = EffinitiveFramework.Core.Http2.Http2RequestConverter.ConvertToHttp1Request(
            Http2Headers(), Array.Empty<byte>(), IPAddress.Parse("203.0.113.8"));

        Assert.False(limiter.TryRejectGlobal(first, new HttpResponse()));
        Assert.True(limiter.TryRejectGlobal(second, new HttpResponse()));
        Assert.False(limiter.TryRejectGlobal(otherClient, new HttpResponse()));
    }

    [Fact]
    public void CachedAddressTextAndIpAddressAgree()
    {
        // The connection caches the formatted address to avoid a per-request allocation; if the two
        // ever disagreed, HTTP/1.1 and HTTP/2 clients would land in different buckets.
        var request = new HttpRequest
        {
            RemoteIpAddress = IPAddress.Parse("203.0.113.7"),
            RemoteIpAddressText = "203.0.113.7"
        };

        var limiter = Limiter();
        var withCache = limiter.ResolvePartitionKey(request);

        request.RemoteIpAddressText = null;
        var withoutCache = limiter.ResolvePartitionKey(request);

        Assert.Equal(withoutCache, withCache);
    }
}
