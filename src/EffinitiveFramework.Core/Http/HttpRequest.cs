using System.Buffers;
using EffinitiveFramework.Core.Authentication;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Represents a parsed HTTP request with minimal allocations
/// </summary>
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
        User = null;
        Items?.Clear();
        RouteValues?.Clear();
        BodyDeferred = false;
        BodyStream = null;
        _cookies = null;
        _query = null;
    }
}

/// <summary>
/// Lazily-created query parameter collection with typed accessors.
/// </summary>
public sealed class QueryCollection : IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, string> _values;

    public static QueryCollection Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private QueryCollection(Dictionary<string, string> values)
    {
        _values = values;
    }

    internal static QueryCollection Parse(string path)
    {
        var queryIndex = path.IndexOf('?');
        if (queryIndex < 0 || queryIndex == path.Length - 1)
        {
            return Empty;
        }

        var query = path.AsSpan(queryIndex + 1);
        var fragmentIndex = query.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            query = query[..fragmentIndex];
        }

        if (query.IsEmpty)
        {
            return Empty;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (!query.IsEmpty)
        {
            var separatorIndex = query.IndexOf('&');
            var pair = separatorIndex >= 0 ? query[..separatorIndex] : query;
            query = separatorIndex >= 0 ? query[(separatorIndex + 1)..] : ReadOnlySpan<char>.Empty;

            if (pair.IsEmpty)
            {
                continue;
            }

            var equalsIndex = pair.IndexOf('=');
            var name = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
            if (name.IsEmpty)
            {
                continue;
            }

            var value = equalsIndex >= 0 ? pair[(equalsIndex + 1)..] : ReadOnlySpan<char>.Empty;
            values[Uri.UnescapeDataString(name.ToString())] = Uri.UnescapeDataString(value.ToString());
        }

        return values.Count == 0 ? Empty : new QueryCollection(values);
    }

    public string? Get(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int defaultValue = 0)
        => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    public double GetDouble(string key, double defaultValue = 0)
        => _values.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : defaultValue;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public int Count => _values.Count;

    public string this[string key] => _values[key];

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _values.GetEnumerator();
}
