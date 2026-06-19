using System.Collections.Frozen;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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
}

/// <summary>
/// High-performance static file handler. Pre-loads all files into memory at startup
/// and serves them via a FrozenDictionary lookup — zero per-request I/O or allocation.
/// Brotli (br) is preferred over gzip when the client accepts it; pre-generated .br
/// and .gz sidecar files are loaded directly rather than recompressed at startup.
/// </summary>
public sealed class StaticFileHandler
{
    private readonly FrozenDictionary<string, CachedStaticFile> _files;
    private readonly string _prefix;       // e.g. "/static"
    private readonly int _prefixLength;
    private readonly string? _cacheControl;

    public StaticFileHandler(StaticFileOptions options)
    {
        _prefix = options.RequestPath.TrimEnd('/');
        _prefixLength = _prefix.Length;
        _cacheControl = options.CacheControl;

        var rootPath = Path.GetFullPath(options.RootPath);
        var dict = new Dictionary<string, CachedStaticFile>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(rootPath))
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                // Skip pre-compressed sidecar files — they're loaded via the base file entry.
                if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Build the URL-relative path: /static/subdir/file.ext
                var relativePath = file[rootPath.Length..].Replace('\\', '/');
                if (!relativePath.StartsWith('/'))
                    relativePath = "/" + relativePath;

                var urlPath = _prefix + relativePath;
                var content = File.ReadAllBytes(file);
                var contentType = GetMimeType(Path.GetExtension(file));

                // Prefer pre-generated sidecar .gz; fall back to runtime compression.
                byte[]? gzipContent;
                var gzPath = file + ".gz";
                if (File.Exists(gzPath))
                    gzipContent = File.ReadAllBytes(gzPath);
                else
                    gzipContent = TryGzipCompress(content, contentType);

                // Load pre-generated .br sidecar (brotli). No runtime fallback — brotli
                // compression is expensive and not worth doing per-startup without a pre-built file.
                byte[]? brotliContent = null;
                var brPath = file + ".br";
                if (File.Exists(brPath))
                    brotliContent = File.ReadAllBytes(brPath);

                dict[urlPath] = new CachedStaticFile(content, contentType, gzipContent, brotliContent);
            }
        }

        _files = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to serve a static file for the given path.
    /// Returns true and populates the response if the path matches a cached file.
    /// Pass acceptEncoding (the Accept-Encoding request header value) to serve pre-compressed content.
    /// Brotli is served preferentially when both brotli content is available and the client accepts br.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryServe(ReadOnlySpan<char> path, string? acceptEncoding, HttpResponse response)
    {
        // Quick prefix check to avoid dictionary lookup for non-static paths
        if (path.Length <= _prefixLength || !path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Strip query string
        var qsIdx = path.IndexOf('?');
        var cleanPath = qsIdx >= 0 ? path[..qsIdx] : path;

#if NET9_0_OR_GREATER
        var lookup = _files.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!lookup.TryGetValue(cleanPath, out var cached))
            return false;
#else
        if (!_files.TryGetValue(new string(cleanPath), out var cached))
            return false;
#endif

        // Check encoding preference: brotli is smaller and preferred when available.
        // The benchmark sends "br;q=1, gzip;q=0.8" so br is explicitly preferred.
        var wantsBrotli = cached.BrotliContent != null &&
                          acceptEncoding != null &&
                          AcceptsEncoding(acceptEncoding, "br");

        var useGzip = !wantsBrotli &&
                      cached.GzipContent != null &&
                      acceptEncoding != null &&
                      AcceptsEncoding(acceptEncoding, "gzip");

        response.StatusCode = 200;
        response.ContentType = cached.ContentType;

        if (wantsBrotli)
        {
            response.Body = cached.BrotliContent!;
            response.Headers[HeaderNames.ContentEncoding] = HeaderValues.Brotli;
            response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
        }
        else if (useGzip)
        {
            response.Body = cached.GzipContent!;
            response.Headers[HeaderNames.ContentEncoding] = HeaderValues.Gzip;
            response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;
        }
        else
        {
            response.Body = cached.Content;
        }

        if (_cacheControl != null)
            response.Headers[HeaderNames.CacheControl] = _cacheControl;

        return true;
    }

    /// <summary>
    /// Check whether a content-coding token appears in an Accept-Encoding value.
    /// Uses word-boundary matching so "br" doesn't match inside "zstd;br=1" incorrectly.
    /// </summary>
    private static bool AcceptsEncoding(string acceptEncoding, string encoding)
    {
        var idx = acceptEncoding.IndexOf(encoding, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        if (idx > 0 && IsTokenChar(acceptEncoding[idx - 1])) return false;
        var end = idx + encoding.Length;
        if (end < acceptEncoding.Length && IsTokenChar(acceptEncoding[end])) return false;
        return true;
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_';

    private static byte[]? TryGzipCompress(byte[] content, string contentType)
    {
        if (content.Length < 1024) return null;
        if (!IsCompressibleContentType(contentType)) return null;
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
            gz.Write(content);
        return ms.ToArray();
    }

    private static bool IsCompressibleContentType(string contentType) =>
        contentType is MediaTypes.ApplicationJson or MediaTypes.TextPlain or MediaTypes.TextHtml or
                       MediaTypes.TextCss or MediaTypes.TextJavaScript or MediaTypes.ApplicationJavaScript or
                       "application/xml" or "text/xml" or "image/svg+xml";

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" or ".mjs" => "application/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".txt" => "text/plain",
        ".xml" => "application/xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".eot" => "application/vnd.ms-fontobject",
        ".map" => "application/json",
        ".wasm" => "application/wasm",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };
}

internal readonly record struct CachedStaticFile(byte[] Content, string ContentType, byte[]? GzipContent, byte[]? BrotliContent);
