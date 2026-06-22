using System.Globalization;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.StaticFiles;

/// <summary>
/// Configuration for static file serving.
/// </summary>
public sealed class StaticFileOptions
{
    /// <summary>
    /// Root directory on disk to serve files from.
    /// </summary>
    public string RootPath { get; set; } = "wwwroot";

    /// <summary>
    /// URL path prefix (e.g. "/static"). Requests matching this prefix are served as static files.
    /// </summary>
    public string RequestPath { get; set; } = "/static";

    /// <summary>
    /// Cache-Control header value. Null to omit.
    /// </summary>
    public string? CacheControl { get; set; } = "public, max-age=3600";

    /// <summary>
    /// File name served when a request resolves to a directory (or the prefix root).
    /// Set to null to disable directory default files.
    /// </summary>
    public string? DefaultFileName { get; set; } = "index.html";
}

/// <summary>
/// Serves static files from disk, one request at a time. Files are streamed straight from
/// the filesystem (never preloaded into the managed heap), so content always reflects what is
/// on disk and memory use is bounded regardless of how large the content directory is.
/// <para>
/// Implements the parts of RFC 9110 a static origin server is expected to honor:
/// <list type="bullet">
///   <item><description><c>ETag</c> / <c>Last-Modified</c> with <c>If-None-Match</c> / <c>If-Modified-Since</c> → <c>304 Not Modified</c>.</description></item>
///   <item><description><c>Range</c> / <c>If-Range</c> → <c>206 Partial Content</c> (single range) or <c>416 Range Not Satisfiable</c>.</description></item>
///   <item><description><c>Accept-Encoding</c> negotiation (respecting q-values) serving pre-generated <c>.br</c> / <c>.gz</c> sidecar files when present.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class StaticFileHandler
{
    // Pre-compressed sidecar file extensions (e.g. site.css.br, site.css.gz). Note the gzip
    // file extension (.gz) differs from its content-coding token (gzip), so these are explicit.
    private const string BrotliExtension = ".br";
    private const string GzipExtension = ".gz";
    // Range unit prefix, e.g. "bytes=0-1023".
    private const string ByteRangeUnit = HeaderValues.Bytes + "=";
    // ETag / If-None-Match comparison tokens (RFC 9110 §8.8.3).
    private const string ETagWildcard = "*";
    private const string WeakETagPrefix = "W/";
    // RFC 1123 / IMF-fixdate format used for Last-Modified.
    private const string HttpDateFormat = "R";

    private readonly string _prefix;          // e.g. "/static"
    private readonly int _prefixLength;
    private readonly string _rootWithSep;     // canonical root, terminated with a directory separator
    private readonly bool _rootExists;
    private readonly string? _cacheControl;
    private readonly string? _defaultFileName;

    public StaticFileHandler(StaticFileOptions options)
    {
        _prefix = options.RequestPath.TrimEnd('/');
        _prefixLength = _prefix.Length;
        _cacheControl = options.CacheControl;
        _defaultFileName = string.IsNullOrEmpty(options.DefaultFileName) ? null : options.DefaultFileName;

        var root = Path.GetFullPath(options.RootPath);
        _rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        _rootExists = Directory.Exists(root);
    }

    /// <summary>
    /// Tries to serve a static file for the given request. Returns true and populates the
    /// response (200/206/304) when the request maps to a file under the configured root;
    /// returns false to let the request fall through to routing.
    /// Only GET and HEAD should be dispatched here.
    /// </summary>
    public bool TryServe(HttpRequest request, HttpResponse response)
    {
        if (!_rootExists)
            return false;

        var path = request.Path.AsSpan();

        // Quick prefix check to avoid any work for non-static paths.
        if (path.Length <= _prefixLength || !path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Strip query string and the request-path prefix.
        var qsIdx = path.IndexOf('?');
        var relative = (qsIdx >= 0 ? path[..qsIdx] : path)[_prefixLength..];

        // Resolve the relative URL path to a physical file, rejecting traversal attempts.
        if (!TryResolvePhysicalPath(relative.ToString(), out var physicalPath))
            return false;

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
            return false;

        var isHead = request.Method.Equals(HttpMethods.Head, StringComparison.OrdinalIgnoreCase);
        var contentType = GetMimeType(Path.GetExtension(physicalPath));

        // Negotiate a pre-compressed representation if a sidecar exists and the client accepts it.
        // Identity ranges are only offered for the uncompressed representation.
        var hasGz = File.Exists(physicalPath + GzipExtension);
        var hasBr = File.Exists(physicalPath + BrotliExtension);
        var encoding = NegotiateEncoding(request, hasBr, hasGz);

        var servedPath = encoding switch
        {
            HeaderValues.Brotli => physicalPath + BrotliExtension,
            HeaderValues.Gzip => physicalPath + GzipExtension,
            _ => physicalPath
        };

        var servedInfo = encoding == null ? fileInfo : new FileInfo(servedPath);
        if (!servedInfo.Exists)
            return false; // sidecar vanished between the existence check and now

        var length = servedInfo.Length;
        // HTTP dates have one-second resolution; truncate so ETag and Last-Modified agree with the header.
        var lastModifiedUtc = TruncateToSeconds(servedInfo.LastWriteTimeUtc);
        var etag = ComputeETag(lastModifiedUtc, length);

        // ---- Conditional request handling (RFC 9110 §13) ----
        if (IsNotModified(request, etag, lastModifiedUtc))
        {
            response.StatusCode = 304;
            response.Body = null;
            response.Headers[HeaderNames.ETag] = etag;
            if (_cacheControl != null)
                response.Headers[HeaderNames.CacheControl] = _cacheControl;
            return true;
        }

        // ---- Common response headers ----
        response.StatusCode = 200;
        response.ContentType = contentType;
        response.Headers[HeaderNames.ETag] = etag;
        response.Headers[HeaderNames.LastModified] = lastModifiedUtc.ToString(HttpDateFormat, CultureInfo.InvariantCulture);
        if (_cacheControl != null)
            response.Headers[HeaderNames.CacheControl] = _cacheControl;

        if (encoding != null)
        {
            response.Headers[HeaderNames.ContentEncoding] = encoding;
            response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
        }
        else if (hasBr || hasGz)
        {
            // An identity response that has compressed siblings still varies by Accept-Encoding,
            // so shared caches don't serve it to a client that should have gotten brotli/gzip.
            response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
        }

        // Range requests are only meaningful for the identity representation.
        long offset = 0;
        var count = length;
        if (encoding == null)
        {
            response.Headers[HeaderNames.AcceptRanges] = HeaderValues.Bytes;

            var rangeResult = EvaluateRange(request, length, etag, lastModifiedUtc, out offset, out count);
            if (rangeResult == RangeResult.NotSatisfiable)
            {
                response.StatusCode = 416;
                response.Headers[HeaderNames.ContentRange] = $"{HeaderValues.Bytes} */{length}";
                response.Body = Array.Empty<byte>();
                return true;
            }
            if (rangeResult == RangeResult.Partial)
            {
                response.StatusCode = 206;
                response.Headers[HeaderNames.ContentRange] = $"{HeaderValues.Bytes} {offset}-{offset + count - 1}/{length}";
            }
        }

        // HEAD: emit identical headers (including Content-Length) but no body.
        if (isHead)
        {
            response.Headers[HeaderNames.ContentLength] = count.ToString(CultureInfo.InvariantCulture);
            response.Body = null;
            return true;
        }

        // Open the served file for streaming. The response writer copies exactly `count` bytes
        // and disposes the stream; if anything fails before we hand it off we dispose it here so
        // the OS file handle is never leaked.
        FileStream? stream = null;
        try
        {
            stream = new FileStream(servedPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

            if (offset > 0)
                stream.Seek(offset, SeekOrigin.Begin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return false; // file disappeared / locked / inaccessible between stat and open
        }

        response.BodyStream = stream;
        response.BodyStreamLength = count;
        return true;
    }

    /// <summary>
    /// Maps a URL-relative path to a physical file path under the root, rejecting any path that
    /// would escape the root (directory traversal). Returns false for unsafe or non-mappable paths.
    /// </summary>
    private bool TryResolvePhysicalPath(string relativeUrlPath, out string physicalPath)
    {
        physicalPath = string.Empty;

        // Percent-decode first so encoded traversal sequences (e.g. %2e%2e) are caught below.
        var decoded = Uri.UnescapeDataString(relativeUrlPath);
        if (decoded.IndexOf('\0') >= 0)
            return false;

        // Walk segments, rejecting "." and ".." so the result can never climb above the root.
        var segments = decoded.Split('/', '\\');
        var safe = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                return false;
            safe.Add(segment);
        }

        var relative = safe.Count == 0 ? string.Empty : string.Join(Path.DirectorySeparatorChar, safe);
        var combined = Path.GetFullPath(Path.Combine(_rootWithSep, relative));

        // If the request targets a directory (including the prefix root), serve its default file.
        if (relative.Length == 0 || Directory.Exists(combined))
        {
            if (_defaultFileName == null)
                return false;
            combined = Path.GetFullPath(Path.Combine(combined, _defaultFileName));
        }

        // Defense in depth: the canonical path must still live under the root.
        if (!combined.StartsWith(_rootWithSep, OrdinalPathComparison))
            return false;

        physicalPath = combined;
        return true;
    }

    /// <summary>
    /// Selects a pre-compressed representation honoring Accept-Encoding q-values, preferring
    /// brotli over gzip when the client weights them equally. Returns "br", "gzip", or null (identity).
    /// </summary>
    private static string? NegotiateEncoding(HttpRequest request, bool hasBr, bool hasGz)
    {
        if (!hasBr && !hasGz)
            return null;
        if (!request.Headers.TryGetValue(HeaderNames.AcceptEncoding, out var acceptEncoding))
            return null;

        // Advertise in server-preference order; SelectEncoding picks the client's highest-q match
        // and correctly drops codings the client rejected with q=0.
        var available = (hasBr, hasGz) switch
        {
            (true, true) => new[] { HeaderValues.Brotli, HeaderValues.Gzip },
            (true, false) => new[] { HeaderValues.Brotli },
            _ => new[] { HeaderValues.Gzip }
        };

        return ContentNegotiation.SelectEncoding(acceptEncoding, available);
    }

    private static bool IsNotModified(HttpRequest request, string etag, DateTime lastModifiedUtc)
    {
        // If-None-Match takes precedence over If-Modified-Since (RFC 9110 §13.1.2 / §13.2.2).
        if (request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch))
            return ETagMatches(ifNoneMatch, etag);

        if (request.Headers.TryGetValue(HeaderNames.IfModifiedSince, out var ifModifiedSince) &&
            TryParseHttpDate(ifModifiedSince, out var since))
        {
            // Truncated last-modified <= client's copy timestamp ⇒ unchanged.
            return lastModifiedUtc <= since;
        }

        return false;
    }

    private static bool ETagMatches(string ifNoneMatch, string etag)
    {
        var value = ifNoneMatch.AsSpan().Trim();
        if (value.SequenceEqual(ETagWildcard))
            return true;

        foreach (var range in value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Compare ignoring a weak ("W/") prefix, per weak comparison for If-None-Match.
            var candidate = range.StartsWith(WeakETagPrefix, StringComparison.Ordinal) ? range[WeakETagPrefix.Length..] : range;
            if (candidate == etag)
                return true;
        }
        return false;
    }

    private enum RangeResult { None, Partial, NotSatisfiable }

    /// <summary>
    /// Evaluates a single-range Range request, honoring If-Range. Multi-range requests are
    /// declined (treated as a full response) since this handler serves a single contiguous range.
    /// </summary>
    private static RangeResult EvaluateRange(HttpRequest request, long length, string etag, DateTime lastModifiedUtc,
        out long offset, out long count)
    {
        offset = 0;
        count = length;

        if (!request.Headers.TryGetValue(HeaderNames.Range, out var rangeHeader))
            return RangeResult.None;

        // If-Range: only honor the range when the validator still matches; otherwise send the full file.
        if (request.Headers.TryGetValue(HeaderNames.IfRange, out var ifRange))
        {
            var trimmed = ifRange.Trim();
            var matches = trimmed.StartsWith('"') || trimmed.StartsWith(WeakETagPrefix, StringComparison.Ordinal)
                ? ETagMatches(trimmed, etag)
                : TryParseHttpDate(trimmed, out var d) && d >= lastModifiedUtc;
            if (!matches)
                return RangeResult.None;
        }

        return ParseSingleByteRange(rangeHeader, length, out offset, out count);
    }

    /// <summary>
    /// Parses a single byte range. Unparseable or multi-range specs are ignored (serve the full file);
    /// only a well-formed range that lies entirely past the end of the file is unsatisfiable (416).
    /// </summary>
    private static RangeResult ParseSingleByteRange(string header, long length, out long offset, out long count)
    {
        offset = 0;
        count = length;

        var span = header.AsSpan().Trim();
        if (!span.StartsWith(ByteRangeUnit))
            return RangeResult.None;

        var spec = span[ByteRangeUnit.Length..];
        if (spec.Contains(','))
            return RangeResult.None; // multiple ranges unsupported — fall back to full response

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return RangeResult.None;

        var startSpan = spec[..dash].Trim();
        var endSpan = spec[(dash + 1)..].Trim();

        if (startSpan.IsEmpty)
        {
            // Suffix range: "-N" = last N bytes.
            if (!long.TryParse(endSpan, out var suffix) || suffix <= 0)
                return RangeResult.None;
            if (length == 0)
                return RangeResult.NotSatisfiable;
            if (suffix > length) suffix = length;
            offset = length - suffix;
            count = suffix;
            return RangeResult.Partial;
        }

        if (!long.TryParse(startSpan, out var start) || start < 0)
            return RangeResult.None;
        if (start >= length)
            return RangeResult.NotSatisfiable; // well-formed but past EOF

        long end;
        if (endSpan.IsEmpty)
            end = length - 1;
        else if (!long.TryParse(endSpan, out end) || end < start)
            return RangeResult.None;

        if (end >= length) end = length - 1;

        offset = start;
        count = end - start + 1;
        return RangeResult.Partial;
    }

    /// <summary>
    /// ETag derived from last-write time and length, matching ASP.NET Core's static file middleware.
    /// Two files with the same size and modification time produce the same (strong) tag.
    /// </summary>
    private static string ComputeETag(DateTime lastModifiedUtc, long length)
    {
        var hash = lastModifiedUtc.ToFileTimeUtc() ^ length;
        return string.Concat("\"", hash.ToString("x", CultureInfo.InvariantCulture), "\"");
    }

    private static DateTime TruncateToSeconds(DateTime value)
        => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static bool TryParseHttpDate(string value, out DateTime utc)
    {
        // RFC 9110 §5.6.7 preferred form plus the two obsolete formats servers must still accept.
        string[] formats = { HttpDateFormat, "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM  d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
        {
            utc = TruncateToSeconds(utc);
            return true;
        }
        utc = default;
        return false;
    }

    // Paths are compared case-insensitively on Windows/macOS, case-sensitively elsewhere.
    private static StringComparison OrdinalPathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".html" or ".htm" => MediaTypes.TextHtml,
        ".css" => MediaTypes.TextCss,
        ".js" or ".mjs" => MediaTypes.TextJavaScript,
        ".json" => MediaTypes.ApplicationJson,
        ".png" => MediaTypes.ImagePng,
        ".jpg" or ".jpeg" => MediaTypes.ImageJpeg,
        ".gif" => MediaTypes.ImageGif,
        ".svg" => MediaTypes.ImageSvgXml,
        ".webp" => MediaTypes.ImageWebp,
        ".ico" => MediaTypes.ImageXIcon,
        ".txt" => MediaTypes.TextPlain,
        ".xml" => MediaTypes.ApplicationXml,
        ".woff" => MediaTypes.FontWoff,
        ".woff2" => MediaTypes.FontWoff2,
        ".ttf" => MediaTypes.FontTtf,
        ".otf" => MediaTypes.FontOtf,
        ".eot" => MediaTypes.ApplicationVndMsFontObject,
        ".map" => MediaTypes.ApplicationJson,
        ".wasm" => MediaTypes.ApplicationWasm,
        ".pdf" => MediaTypes.ApplicationPdf,
        ".zip" => MediaTypes.ApplicationZip,
        _ => MediaTypes.ApplicationOctetStream
    };
}
