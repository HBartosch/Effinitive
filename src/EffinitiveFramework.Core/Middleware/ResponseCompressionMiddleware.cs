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
            MediaTypes.ApplicationJson,
            MediaTypes.TextPlain,
            MediaTypes.TextHtml,
            MediaTypes.TextCss,
            MediaTypes.TextJavaScript,
            MediaTypes.ApplicationJavaScript,
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
        response.Headers[HeaderNames.ContentEncoding] = HeaderValues.Gzip;
        // Append rather than assign: an inner concern (e.g. a cached endpoint varying by
        // Accept-Language) may already have registered its own Vary fields.
        response.AppendVary(HeaderNames.AcceptEncoding);
        return response;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldCompress(HttpRequest request, HttpResponse response)
    {
        // Don't compress streaming responses
        if (response.IsStreaming)
            return false;

        // Everything that can be decided from the request and the headers is decided first.
        // Only the minimum-size guard needs the body, and a body that is still an object has
        // to be serialized to be measured. Serializing it here also costs the writer its
        // single-pass pooled path, so that question is asked last and only when the answer
        // can still change the outcome.

        // A client that offered no coding takes identity (RFC 9110 §12.5.3).
        if (!request.Headers.TryGetValue(HeaderNames.AcceptEncoding, out var acceptEncoding))
            return false;

        if (acceptEncoding.IndexOf(HeaderValues.Gzip, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        // Don't compress if already encoded
        var headers = response.HeadersOrNull;
        if (headers != null && headers.ContainsKey(HeaderNames.ContentEncoding))
            return false;

        if (!IsCompressibleContentType(response.ContentType))
            return false;

        if (response.BodyObject != null)
            response.MaterializeDeferredBody();

        return response.Body != null && response.Body.Length >= _minimumSize;
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
