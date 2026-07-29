using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// Applies token-bucket rate limits per client address.
/// <para>
/// Evaluated at two points in <c>HandleRequestAsync</c>: the global policy before routing, so floods at
/// unrouted paths are rejected without doing lookup work, and any endpoint policy once the route is
/// known. Both apply — an endpoint limit narrows protection rather than replacing it.
/// </para>
/// </summary>
public sealed class RateLimiter
{
    private readonly RateLimitOptions _options;
    private readonly IRateLimitStore _store;
    private readonly RateLimitPolicy _globalPolicy;
    private readonly JsonSerializerOptions? _jsonOptions;

    // Attribute lookup happens once per endpoint type, not once per request — the same memoization
    // ResponseCacheMiddleware uses for [ResponseCache].
    private readonly ConcurrentDictionary<Type, EndpointRateLimit> _endpointPolicies = new();

    internal RateLimiter(RateLimitOptions options, JsonSerializerOptions? jsonOptions)
    {
        _options = options;
        _jsonOptions = jsonOptions;
        _store = _options.Store ??= new MemoryRateLimitStore(options.MaxTrackedClients);
        _globalPolicy = new RateLimitPolicy(options.PermitLimit, options.Window, "global");
    }

    /// <summary>The backing store, exposed for diagnostics.</summary>
    public IRateLimitStore Store => _store;

    /// <summary>
    /// Applies the server-wide limit. Returns true when the request was rejected, in which case
    /// <paramref name="response"/> has been populated and the caller should stop.
    /// </summary>
    public bool TryRejectGlobal(HttpRequest request, HttpResponse response)
        => TryReject(request, response, _globalPolicy);

    /// <summary>
    /// Applies an endpoint's own limit, if it declares one. Returns true when rejected.
    /// </summary>
    public bool TryRejectEndpoint(HttpRequest request, HttpResponse response, Type endpointType)
    {
        var endpoint = _endpointPolicies.GetOrAdd(endpointType, static type => EndpointRateLimit.FromAttributes(type));

        if (endpoint.Exempt || endpoint.Policy == null)
            return false;

        return TryReject(request, response, endpoint.Policy);
    }

    /// <summary>Whether the endpoint opted out of limiting entirely.</summary>
    public bool IsExempt(Type endpointType)
        => _endpointPolicies.GetOrAdd(endpointType, static type => EndpointRateLimit.FromAttributes(type)).Exempt;

    private bool TryReject(HttpRequest request, HttpResponse response, RateLimitPolicy policy)
    {
        var partition = ResolvePartitionKey(request);
        var nowTicks = DateTime.UtcNow.Ticks;

        if (_store.TryAcquire(partition, policy, nowTicks, out var retryAfterSeconds, out var remaining))
            return false;

        WriteRejection(response, policy, retryAfterSeconds, remaining);
        return true;
    }

    // ── Client identity ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The key a limit is counted against: the peer address, or the forwarded client address when the
    /// peer is a configured reverse proxy.
    /// </summary>
    internal string ResolvePartitionKey(HttpRequest request)
    {
        var peer = request.RemoteIpAddress;

        if (_options.TrustForwardedHeaders && peer != null && IsTrustedProxy(peer))
        {
            var forwarded = ResolveForwardedFor(request);
            if (forwarded != null)
                return forwarded;
        }

        // Prefer the string the connection already formatted — IPAddress.ToString() allocates, and this
        // runs on every request.
        if (request.RemoteIpAddressText != null)
            return request.RemoteIpAddressText;

        // No address at all (in-memory transports, tests): fall back to a single shared bucket rather
        // than throwing or, worse, letting every such request through unlimited.
        return peer?.ToString() ?? "unknown";
    }

    private bool IsTrustedProxy(IPAddress peer)
    {
        var trusted = _options.TrustedProxies;

        // Enabling forwarded headers without naming any proxy is a misconfiguration, not permission to
        // trust every caller — treat it as "trust nobody".
        for (int i = 0; i < trusted.Count; i++)
        {
            if (trusted[i].Equals(peer))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Takes the client from <c>X-Forwarded-For</c>, reading right-to-left and skipping entries that are
    /// themselves trusted proxies. Walking from the right matters: the leftmost entries are whatever the
    /// original caller sent and can be forged, while each proxy appends the peer it actually saw.
    /// </summary>
    private string? ResolveForwardedFor(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.XForwardedFor, out var header) || string.IsNullOrEmpty(header))
            return null;

        var entries = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = entries.Length - 1; i >= 0; i--)
        {
            if (!IPAddress.TryParse(StripPort(entries[i]), out var candidate))
                continue;

            if (candidate.IsIPv4MappedToIPv6)
                candidate = candidate.MapToIPv4();

            if (IsTrustedProxy(candidate))
                continue;   // another hop in our own chain

            return candidate.ToString();
        }

        return null;
    }

    /// <summary>
    /// Strips a port from an X-Forwarded-For entry. IPv6 entries are bracketed ("[::1]:443"); IPv4 may
    /// carry a trailing ":port", but a bare IPv6 address also contains colons and must be left alone.
    /// </summary>
    private static string StripPort(string entry)
    {
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']');
            return close > 0 ? entry[1..close] : entry;
        }

        var colon = entry.IndexOf(':');
        if (colon > 0 && entry.IndexOf(':', colon + 1) < 0)
            return entry[..colon];   // exactly one colon, so IPv4:port

        return entry;
    }

    // ── Rejection ───────────────────────────────────────────────────────────────────────────────

    private void WriteRejection(HttpResponse response, RateLimitPolicy policy, int retryAfterSeconds, int remaining)
    {
        response.StatusCode = _options.RejectionStatusCode;
        response.ContentType = MediaTypes.ApplicationProblemJson;
        response.Headers[HeaderNames.RetryAfter] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        if (_options.EmitRateLimitHeaders)
        {
            response.Headers[HeaderNames.XRateLimitLimit] = policy.PermitLimit.ToString(CultureInfo.InvariantCulture);
            response.Headers[HeaderNames.XRateLimitRemaining] = remaining.ToString(CultureInfo.InvariantCulture);
            response.Headers[HeaderNames.XRateLimitReset] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/429",
            Title = "Too Many Requests",
            Status = _options.RejectionStatusCode,
            Detail = string.Create(CultureInfo.InvariantCulture,
                $"Rate limit exceeded. Retry in {retryAfterSeconds} second(s).")
        };

        response.Body = JsonSerializer.SerializeToUtf8Bytes(problem, _jsonOptions);
    }

    // ── Endpoint policy ─────────────────────────────────────────────────────────────────────────

    private readonly struct EndpointRateLimit
    {
        public RateLimitPolicy? Policy { get; init; }
        public bool Exempt { get; init; }

        public static EndpointRateLimit FromAttributes(Type endpointType)
        {
            if (endpointType.GetCustomAttribute<DisableRateLimitAttribute>() != null)
                return new EndpointRateLimit { Exempt = true };

            var attribute = endpointType.GetCustomAttribute<RateLimitAttribute>();
            if (attribute == null)
                return default;

            // Guard against a nonsensical attribute taking the server down at startup.
            if (attribute.PermitLimit <= 0 || attribute.WindowSeconds <= 0)
                return default;

            return new EndpointRateLimit
            {
                Policy = new RateLimitPolicy(
                    attribute.PermitLimit,
                    TimeSpan.FromSeconds(attribute.WindowSeconds),
                    endpointType.FullName ?? endpointType.Name)
            };
        }
    }
}
