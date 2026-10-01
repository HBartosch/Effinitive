namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Canonical instances of the tokens a request repeats, matched straight from the wire bytes.
/// </summary>
/// <remarks>
/// A method, an HTTP version and a header field name are each drawn from a small known set,
/// so decoding those bytes produces a string the server already holds as a constant. Matching
/// first and decoding only on a miss keeps the common request from allocating a string whose
/// contents were never in question.
///
/// Field names are case-insensitive (RFC 9110 5.1), so matching them is too, and the canonical
/// spelling is what gets stored. The request header dictionary already compares its keys with
/// OrdinalIgnoreCase, so this changes no lookup. Methods and versions are case-sensitive
/// (RFC 9110 9.1, RFC 9112 2.3) and are matched exactly.
/// </remarks>
internal static class WellKnownTokens
{
    /// <summary>The canonical method string, or null when the bytes are not a method this knows.</summary>
    public static string? Method(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
        case 3:
            if (bytes.SequenceEqual("GET"u8)) return HttpMethods.Get;
            if (bytes.SequenceEqual("PUT"u8)) return HttpMethods.Put;
            return null;
        case 4:
            if (bytes.SequenceEqual("HEAD"u8)) return HttpMethods.Head;
            if (bytes.SequenceEqual("POST"u8)) return HttpMethods.Post;
            return null;
        case 5:
            if (bytes.SequenceEqual("PATCH"u8)) return HttpMethods.Patch;
            if (bytes.SequenceEqual("TRACE"u8)) return HttpMethods.Trace;
            return null;
        case 6:
            if (bytes.SequenceEqual("DELETE"u8)) return HttpMethods.Delete;
            return null;
        case 7:
            if (bytes.SequenceEqual("CONNECT"u8)) return HttpMethods.Connect;
            if (bytes.SequenceEqual("OPTIONS"u8)) return HttpMethods.Options;
            return null;
            default:
                return null;
        }
    }

    /// <summary>The canonical HTTP version string, or null when the bytes are not one this knows.</summary>
    public static string? Version(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
        case 8:
            if (bytes.SequenceEqual("HTTP/1.1"u8)) return HttpVersions.Http11;
            if (bytes.SequenceEqual("HTTP/1.0"u8)) return HttpVersions.Http10;
            return null;
            default:
                return null;
        }
    }

    /// <summary>The canonical field name, or null when the bytes are not a name this knows.</summary>
    public static string? HeaderName(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
        case 2:
            if (Matches(bytes, "te"u8)) return HeaderNames.TE;
            return null;
        case 4:
            if (Matches(bytes, "host"u8)) return HeaderNames.Host;
            return null;
        case 5:
            if (Matches(bytes, "range"u8)) return HeaderNames.Range;
            return null;
        case 6:
            if (Matches(bytes, "accept"u8)) return HeaderNames.Accept;
            if (Matches(bytes, "cookie"u8)) return HeaderNames.Cookie;
            if (Matches(bytes, "expect"u8)) return HeaderNames.Expect;
            if (Matches(bytes, "origin"u8)) return HeaderNames.Origin;
            if (Matches(bytes, "pragma"u8)) return HeaderNames.Pragma;
            return null;
        case 7:
            if (Matches(bytes, "referer"u8)) return HeaderNames.Referer;
            if (Matches(bytes, "upgrade"u8)) return HeaderNames.Upgrade;
            return null;
        case 8:
            if (Matches(bytes, "if-range"u8)) return HeaderNames.IfRange;
            return null;
        case 10:
            if (Matches(bytes, "connection"u8)) return HeaderNames.Connection;
            if (Matches(bytes, "user-agent"u8)) return HeaderNames.UserAgent;
            return null;
        case 12:
            if (Matches(bytes, "content-type"u8)) return HeaderNames.ContentType;
            return null;
        case 13:
            if (Matches(bytes, "authorization"u8)) return HeaderNames.Authorization;
            if (Matches(bytes, "cache-control"u8)) return HeaderNames.CacheControl;
            if (Matches(bytes, "if-none-match"u8)) return HeaderNames.IfNoneMatch;
            return null;
        case 14:
            if (Matches(bytes, "content-length"u8)) return HeaderNames.ContentLength;
            return null;
        case 15:
            if (Matches(bytes, "accept-encoding"u8)) return HeaderNames.AcceptEncoding;
            if (Matches(bytes, "accept-language"u8)) return HeaderNames.AcceptLanguage;
            if (Matches(bytes, "x-forwarded-for"u8)) return HeaderNames.XForwardedFor;
            return null;
        case 16:
            if (Matches(bytes, "content-encoding"u8)) return HeaderNames.ContentEncoding;
            if (Matches(bytes, "x-requested-with"u8)) return HeaderNames.XRequestedWith;
            return null;
        case 17:
            if (Matches(bytes, "if-modified-since"u8)) return HeaderNames.IfModifiedSince;
            if (Matches(bytes, "sec-websocket-key"u8)) return HeaderNames.SecWebSocketKey;
            if (Matches(bytes, "transfer-encoding"u8)) return HeaderNames.TransferEncoding;
            return null;
        case 21:
            if (Matches(bytes, "sec-websocket-version"u8)) return HeaderNames.SecWebSocketVersion;
            return null;
        case 22:
            if (Matches(bytes, "sec-websocket-protocol"u8)) return HeaderNames.SecWebSocketProtocol;
            return null;
        case 24:
            if (Matches(bytes, "sec-websocket-extensions"u8)) return HeaderNames.SecWebSocketExtensions;
            return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// ASCII case-insensitive comparison against an already-lowercase candidate.
    /// </summary>
    /// <remarks>
    /// Only bytes in A-Z are folded. Folding by setting bit 0x20 across the board would be
    /// shorter, but it would also map CR onto the hyphen the longer names contain, and a
    /// field name is not the place to accept a byte that only resembles the right one.
    /// </remarks>
    private static bool Matches(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> lowercase)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte c = bytes[i];
            if (c >= (byte)'A' && c <= (byte)'Z')
                c |= 0x20;
            if (c != lowercase[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// The lowercase form of a field name, for the protocols that require it on the wire.
    /// </summary>
    /// <remarks>
    /// HTTP/2 and HTTP/3 carry field names in lowercase (RFC 9113 §8.2.1, RFC 9114 §4.1.2), while a
    /// response holds them in canonical case, so each one would otherwise be lowercased on its way
    /// out and allocate a string per field per response. The names a server sets come from a known
    /// set, so the mapping is made once at startup and the allocation only remains for a name the
    /// application invented.
    /// </remarks>
    public static string Lowercase(string name)
        => LowercaseNames.TryGetValue(name, out var lower) ? lower : name.ToLowerInvariant();

    private static readonly Dictionary<string, string> LowercaseNames = BuildLowercaseNames();

    private static Dictionary<string, string> BuildLowercaseNames()
    {
        // Ordinal rather than case-insensitive: the point is to map a specific spelling to its
        // lowercase form, and a case-insensitive map would return the wrong casing's entry.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in typeof(HeaderNames).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is string name)
                map[name] = name.ToLowerInvariant();
        }

        return map;
    }

    /// <summary>
    /// The status code rendered as a string, for the codes a server actually sends.
    /// </summary>
    /// <remarks>
    /// HTTP/2 and HTTP/3 carry the status as the value of a :status field, and matching it against
    /// the QPACK or HPACK static table needs it as a string. Calling ToString on every response
    /// allocates one each time, for a value drawn from a range small enough to hold outright.
    /// </remarks>
    public static string StatusCode(int code)
    {
        var index = code - 100;
        return (uint)index < (uint)StatusStrings.Length ? StatusStrings[index] : code.ToString();
    }

    // 100 through 599, which covers every status class RFC 9110 §15 defines.
    private static readonly string[] StatusStrings = BuildStatusStrings();

    private static string[] BuildStatusStrings()
    {
        var strings = new string[500];
        for (int i = 0; i < strings.Length; i++)
            strings[i] = (i + 100).ToString();

        return strings;
    }
}
