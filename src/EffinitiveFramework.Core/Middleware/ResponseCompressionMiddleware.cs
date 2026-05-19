using System.IO.Compression;
using System.Runtime.CompilerServices;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Middleware;

/// <summary>
/// Response compression middleware that marks eligible responses for gzip compression.
/// Actual compression is deferred to HttpResponseWriter, which serializes + compresses
/// in a single pipeline using pooled buffers — matching Kestrel's approach.
/// </summary>
public class ResponseCompressionMiddleware : IMiddleware
{
    private readonly CompressionLevel _compressionLevel;
    private readonly int _minimumSize;
    private readonly string[] _compressibleContentTypes;

    public ResponseCompressionMiddleware(
        CompressionLevel compressionLevel = CompressionLevel.Fastest,
        int minimumSize = 1024,
        string[]? compressibleContentTypes = null)
    {
        _compressionLevel = compressionLevel;
        _minimumSize = minimumSize;
        _compressibleContentTypes = compressibleContentTypes ?? new[]
        {
            "application/json",
            "text/plain",
            "text/html",
            "text/css",
            "text/javascript",
            "application/javascript",
            "application/xml",
            "text/xml"
        };
    }

    public ValueTask<HttpResponse> InvokeAsync(
        HttpRequest request,
        RequestDelegate next,
        CancellationToken cancellationToken)
    {
        return InvokeInternalAsync(request, next, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async ValueTask<HttpResponse> InvokeInternalAsync(
        HttpRequest request,
        RequestDelegate next,
        CancellationToken cancellationToken)
    {
        var response = await next(request, cancellationToken);
        
        if (!ShouldCompress(request, response))
            return response;

        // Don't compress here — just mark the response so the writer can do
        // serialize + compress in one pooled pipeline (like Kestrel).
        response.GzipCompressionLevel = _compressionLevel;
        response.Headers["Content-Encoding"] = "gzip";
        response.Headers["Vary"] = "Accept-Encoding";
        return response;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldCompress(HttpRequest request, HttpResponse response)
    {
        // Don't compress streaming responses
        if (response.IsStreaming)
            return false;

        // When the body is deferred (BodyObject), we can't know the serialized byte count
        // until we materialize it. Materialize now so the minimum-size guard is accurate —
        // the single-pass serialize+compress path in the writer is only worth the overhead
        // for bodies that are large enough to compress meaningfully.
        if (response.BodyObject != null)
            response.MaterializeDeferredBody();

        if (response.Body == null || response.Body.Length < _minimumSize)
            return false;

        // Don't compress if already encoded
        var headers = response.HeadersOrNull;
        if (headers != null && headers.ContainsKey("Content-Encoding"))
            return false;

        // Check if client accepts gzip
        if (!request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding))
            return false;

        if (acceptEncoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return IsCompressibleContentType(response.ContentType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCompressibleContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        var semicolonIndex = contentType.IndexOf(';');
        var mediaType = semicolonIndex >= 0
            ? contentType.AsSpan(0, semicolonIndex).Trim()
            : contentType.AsSpan().Trim();

        for (int i = 0; i < _compressibleContentTypes.Length; i++)
        {
            if (mediaType.Equals(_compressibleContentTypes[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
