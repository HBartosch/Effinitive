using System.Buffers;
using System.Net;
using EffinitiveFramework.Core.Authentication;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Represents a parsed HTTP request with minimal allocations
/// </summary>
/// <remarks>
/// <para>
/// Valid for the duration of the request that is being handled, and no longer. A connection
/// parses every one of its requests into a single instance, resetting it before each, so a
/// reference kept past the end of a handler will be seen to change and then to be reused by
/// the next request on that connection, or by a different peer once the connection is pooled.
/// </para>
/// <para>
/// To carry something into work that outlives the request, copy the values needed and pass
/// those. Do not pass the request itself to a background task, and do not capture it in a
/// continuation that is not awaited before the handler returns.
/// </para>
/// </remarks>
public sealed class HttpRequest
{
    private string _path = string.Empty;

    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS)
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Raw request target path. May include a query string (for example, "/api/users?page=2").
    /// </summary>
    public string Path
    {
        get => _path;
        set
        {
            _path = value;
            _query = null;
        }
    }

    /// <summary>
    /// HTTP version (typically "HTTP/1.1")
    /// </summary>
    public string HttpVersion { get; set; } = "HTTP/1.1";

    /// <summary>
    /// Request headers (name -> value)
    /// </summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Request body. Backed by a pooled buffer — valid only for the duration of the request.
    /// Empty when <see cref="BodyDeferred"/> is true (body is larger than the streaming threshold).
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; set; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Internal pooled buffer backing <see cref="Body"/>. Returned to <see cref="System.Buffers.ArrayPool{T}"/> on Reset().
    /// </summary>
    internal byte[]? RentedBodyBuffer { get; set; }

    /// <summary>
    /// When true, the body was not buffered due to its large size. Use <see cref="BodyStream"/> to read it.
    /// </summary>
    public bool BodyDeferred { get; set; }

    /// <summary>
    /// When true, the request uses Transfer-Encoding: chunked. <see cref="BodyStream"/> is a
    /// <see cref="ChunkedBodyStream"/> that dechunks on-the-fly; <see cref="ContentLength"/> is -1.
    /// </summary>
    internal bool IsChunked { get; set; }

    /// <summary>
    /// Streaming access to the request body when <see cref="BodyDeferred"/> is true.
    /// Automatically drained by the framework after the endpoint completes.
    /// </summary>
    public Stream? BodyStream { get; set; }

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
    /// The peer's IP address, as reported by the transport. Null when the connection has no IP endpoint
    /// (in-memory transports and unit tests).
    /// <para>
    /// This is always the address the packets came from, never a value taken from a request header. Code
    /// that needs the originating client behind a reverse proxy must resolve <c>X-Forwarded-For</c>
    /// explicitly and against a trusted-proxy list — the header is client-supplied and trivially spoofed.
    /// </para>
    /// </summary>
    public IPAddress? RemoteIpAddress { get; set; }

    /// <summary>
    /// <see cref="RemoteIpAddress"/> pre-formatted, carried over from the connection so rate limiting
    /// does not pay an <see cref="IPAddress.ToString"/> allocation per request. Internal because it is
    /// a cache of the property above, not a second piece of information.
    /// </summary>
    internal string? RemoteIpAddressText { get; set; }

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

    private Dictionary<string, string>? _cookies;
    private QueryCollection? _query;

    /// <summary>
    /// Parsed query parameters from <see cref="Path"/>. Lazily parsed and cached on first access.
    /// </summary>
    public QueryCollection Query => _query ??= QueryCollection.Parse(_path);

    /// <summary>
    /// Parsed cookies from the Cookie header. Lazily parsed on first access.
    /// </summary>
    public IReadOnlyDictionary<string, string> Cookies
    {
        get
        {
            if (_cookies == null)
            {
                _cookies = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Headers.TryGetValue("Cookie", out var cookieHeader))
                {
                    ParseCookies(cookieHeader, _cookies);
                }
            }
            return _cookies;
        }
    }

    private static void ParseCookies(string header, Dictionary<string, string> cookies)
    {
        // Cookie header: "name=value; name2=value2" or "a=1, b=2" (merged duplicates)
        var span = header.AsSpan();
        while (span.Length > 0)
        {
            // Find next separator (; or ,)
            int sep = -1;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == ';' || span[i] == ',')
                {
                    sep = i;
                    break;
                }
            }

            var pair = sep >= 0 ? span[..sep] : span;
            span = sep >= 0 ? span[(sep + 1)..] : ReadOnlySpan<char>.Empty;

            pair = pair.Trim();
            if (pair.IsEmpty) continue;

            var eq = pair.IndexOf('=');
            if (eq > 0)
            {
                var name = pair[..eq].Trim();
                var value = pair[(eq + 1)..];
                if (name.Length > 0)
                    cookies[name.ToString()] = value.ToString();
            }
        }
    }

    /// <summary>
    /// Reads the full request body, handling both buffered and deferred (chunked / large) bodies.
    /// For small bodies the existing <see cref="Body"/> buffer is returned directly without copying.
    /// For deferred bodies the stream is drained into a new byte array.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadBodyAsync(CancellationToken ct = default)
    {
        if (!BodyDeferred || BodyStream == null)
            return Body;

        // Drain into a pooled buffer and hand back a window onto it rather than a copy. The
        // buffer is released by Reset, so it stays valid for as long as the caller holds the
        // request. A declared length is rented outright; a chunked body has none and grows.
        int initial = ContentLength > 0 && ContentLength <= int.MaxValue ? (int)ContentLength : 8192;
        var buffer = ArrayPool<byte>.Shared.Rent(initial);
        int written = 0;

        while (true)
        {
            if (written == buffer.Length)
            {
                var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Buffer.BlockCopy(buffer, 0, larger, 0, written);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }

            int read = await BodyStream.ReadAsync(buffer.AsMemory(written), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            written += read;
        }

        if (RentedBodyBuffer != null)
            ArrayPool<byte>.Shared.Return(RentedBodyBuffer);
        RentedBodyBuffer = buffer;

        // The body is buffered now, so a second read returns these same octets rather than
        // draining a stream that has nothing left and reporting an empty body.
        Body = buffer.AsMemory(0, written);
        BodyDeferred = false;
        BodyStream = null;
        return Body;
    }

    /// <summary>
    /// Counts the total bytes in the request body without materializing them.
    /// Ideal for upload endpoints that only need the size, not the content.
    /// </summary>
    public async ValueTask<long> CountBodyBytesAsync(CancellationToken ct = default)
    {
        if (!BodyDeferred || BodyStream == null)
            return Body.Length;
        var buf = ArrayPool<byte>.Shared.Rent(65536);
        long count = 0;
        try
        {
            int r;
            while ((r = await BodyStream.ReadAsync(buf.AsMemory(), ct)) > 0)
                count += r;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        return count;
    }

    /// <summary>
    /// Reset the request for reuse from object pool
    /// </summary>
    public void Reset()
    {
        Method = string.Empty;
        Path = string.Empty;
        HttpVersion = "HTTP/1.1";
        Headers.Clear();
        // Return rented body buffer to pool before clearing
        if (RentedBodyBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(RentedBodyBuffer);
            RentedBodyBuffer = null;
        }
        Body = ReadOnlyMemory<byte>.Empty;
        ContentLength = -1;
        KeepAlive = true;
        IsHttps = false;
        RemoteIpAddress = null;
        RemoteIpAddressText = null;
        User = null;
        Items?.Clear();
        RouteValues?.Clear();
        BodyDeferred = false;
        IsChunked = false;
        BodyStream = null;
        _cookies = null;
        _query = null;
    }
}
