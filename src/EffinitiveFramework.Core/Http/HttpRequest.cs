using EffinitiveFramework.Core.Authentication;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Represents a parsed HTTP request with minimal allocations
/// </summary>
public sealed class HttpRequest
{
    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS)
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Request path (e.g., "/api/users")
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// HTTP version (typically "HTTP/1.1")
    /// </summary>
    public string HttpVersion { get; set; } = "HTTP/1.1";

    /// <summary>
    /// Request headers (name -> value)
    /// </summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Request body as byte array
    /// </summary>
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Content-Length header value, -1 if not present
    /// </summary>
    public long ContentLength { get; set; } = -1;

    /// <summary>
    /// Whether the connection should be kept alive
    /// </summary>
    public bool KeepAlive { get; set; } = true; // HTTP/1.1 default

    /// <summary>
    /// Whether this is an HTTPS request
    /// </summary>
    public bool IsHttps { get; set; }

    /// <summary>
    /// The authenticated user (null if not authenticated)
    /// </summary>
    public ClaimsPrincipal? User { get; set; }

    /// <summary>
    /// Dictionary for storing request-scoped metadata (e.g., endpoint type, route parameters)
    /// </summary>
    public Dictionary<string, object>? Items { get; set; }

    /// <summary>
    /// Route parameter values extracted from the URL path (e.g., {id}, {name})
    /// Similar to ASP.NET Core's RouteValues for familiar syntax
    /// </summary>
    public Dictionary<string, string>? RouteValues { get; set; }

    /// <summary>
    /// Reset the request for reuse from object pool
    /// </summary>
    public void Reset()
    {
        Method = string.Empty;
        Path = string.Empty;
        HttpVersion = "HTTP/1.1";
        Headers.Clear();
        Body = Array.Empty<byte>();
        ContentLength = -1;
        KeepAlive = true;
        IsHttps = false;
        User = null;
        Items?.Clear();
        RouteValues?.Clear();
    }
}
