using System.Collections.Frozen;
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
                // Build the URL-relative path: /static/subdir/file.ext
                var relativePath = file[rootPath.Length..].Replace('\\', '/');
                if (!relativePath.StartsWith('/'))
                    relativePath = "/" + relativePath;

                var urlPath = _prefix + relativePath;
                var content = File.ReadAllBytes(file);
                var contentType = GetMimeType(Path.GetExtension(file));

                dict[urlPath] = new CachedStaticFile(content, contentType);
            }
        }

        _files = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to serve a static file for the given path.
    /// Returns true and populates the response if the path matches a cached file.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryServe(ReadOnlySpan<char> path, HttpResponse response)
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

        response.StatusCode = 200;
        response.Body = cached.Content;
        response.ContentType = cached.ContentType;
        if (_cacheControl != null)
            response.Headers["Cache-Control"] = _cacheControl;

        return true;
    }

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

internal readonly record struct CachedStaticFile(byte[] Content, string ContentType);
