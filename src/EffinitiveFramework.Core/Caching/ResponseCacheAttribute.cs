namespace EffinitiveFramework.Core.Caching;

/// <summary>
/// Where a response is allowed to be cached.
/// </summary>
public enum ResponseCacheLocation
{
    /// <summary>
    /// Cacheable by any cache — the origin store, shared proxies, and the client.
    /// Emits <c>Cache-Control: public, max-age=N</c>.
    /// </summary>
    Any = 0,

    /// <summary>
    /// Cacheable by the client only. Emits <c>Cache-Control: private, max-age=N</c> and
    /// is never stored in the server-side response cache.
    /// </summary>
    Client = 1,

    /// <summary>
    /// Not cacheable anywhere. Emits <c>Cache-Control: no-cache, no-store</c>.
    /// </summary>
    None = 2
}

/// <summary>
/// Opts an endpoint into response caching. Without this attribute nothing is stored in the
/// server-side cache, so enabling <c>UseResponseCaching()</c> on an existing app changes no behaviour
/// until endpoints opt in.
/// <para>
/// Applied to the endpoint class (route registration is per type, so a class-level attribute is what
/// the middleware reads):
/// <code>
/// [ResponseCache(Duration = 60, VaryByHeader = "Accept-Language")]
/// public sealed class GetProductsEndpoint : EndpointBase&lt;EmptyRequest, Product[]&gt; { }
/// </code>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class ResponseCacheAttribute : Attribute
{
    /// <summary>
    /// Freshness lifetime in seconds. When left at 0 the store falls back to
    /// <see cref="ResponseCacheOptions.DefaultDuration"/>.
    /// </summary>
    public int Duration { get; set; }

    /// <summary>
    /// Which caches may store the response. Defaults to <see cref="ResponseCacheLocation.Any"/>.
    /// </summary>
    public ResponseCacheLocation Location { get; set; } = ResponseCacheLocation.Any;

    /// <summary>
    /// When true, emits <c>no-store</c> and disables server-side storage regardless of
    /// <see cref="Duration"/>.
    /// </summary>
    public bool NoStore { get; set; }

    /// <summary>
    /// Allows requests carrying an <c>Authorization</c> header to be served from — and stored in — the
    /// shared server-side cache. Off by default: an endpoint that is both <c>[Authorize]</c> and
    /// cached would otherwise key on the path alone and hand one user's response to the next caller.
    /// Only turn this on when the response is identical for every caller regardless of who they are.
    /// </summary>
    public bool AllowAuthenticated { get; set; }

    /// <summary>
    /// Comma-separated request header names that select the representation (e.g.
    /// "Accept-Language, X-Tenant"). Their values are part of the cache key and the names are
    /// emitted in the <c>Vary</c> response header.
    /// </summary>
    public string? VaryByHeader { get; set; }

    /// <summary>
    /// Comma-separated query keys that select the representation. When set, only these keys
    /// participate in the cache key; when null the full query string does.
    /// </summary>
    public string? VaryByQueryKeys { get; set; }
}
