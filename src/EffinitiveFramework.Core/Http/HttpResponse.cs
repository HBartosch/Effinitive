using System.IO.Compression;
using System.Text.Json;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Delegate for streaming response handler
/// </summary>
public delegate Task StreamHandler(Stream stream, CancellationToken cancellationToken);

/// <summary>
/// Represents an HTTP response to be sent
/// </summary>
public sealed class HttpResponse
{
    private Dictionary<string, string>? _headers;
    private string _contentType = "application/json";

    /// <summary>
    /// HTTP status code (200, 404, 500, etc.)
    /// </summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>
    /// Response headers (name -> value)
    /// </summary>
    public Dictionary<string, string> Headers => _headers ??= new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, string>? HeadersOrNull => _headers;

    /// <summary>
    /// Response body as byte array
    /// </summary>
    public byte[]? Body { get; set; }

    /// <summary>
    /// Deferred response body object for single-pass serialization + compression.
    /// When set, the body will be serialized lazily — either through a compression
    /// stream (single-pass) or directly to bytes at write time.
    /// </summary>
    public object? BodyObject { get; set; }

    /// <summary>
    /// JSON serializer options used when materializing BodyObject.
    /// </summary>
    public JsonSerializerOptions? BodySerializerOptions { get; set; }

    /// <summary>
    /// When set, indicates the response body should be gzip-compressed at write time.
    /// The compression middleware sets this instead of compressing eagerly,
    /// allowing the writer to serialize + compress in one pipeline with pooled buffers.
    /// </summary>
    public CompressionLevel? GzipCompressionLevel { get; set; }

    /// <summary>
    /// Stream handler for streaming responses (SSE, chunked transfer, etc.)
    /// If set, Body is ignored and the handler controls the response stream
    /// </summary>
    public StreamHandler? StreamHandler { get; set; }

    /// <summary>
    /// Whether this is a streaming response
    /// </summary>
    public bool IsStreaming => StreamHandler != null;

    /// <summary>
    /// Content type (defaults to application/json)
    /// </summary>
    public string ContentType
    {
        get => _headers != null && _headers.TryGetValue("Content-Type", out var value) ? value : _contentType;
        set
        {
            _contentType = value;
            if (_headers != null)
                _headers["Content-Type"] = value;
        }
    }

    /// <summary>
    /// Whether to keep the connection alive
    /// </summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>
    /// Get status text for status code
    /// </summary>
    public string GetStatusText()
    {
        return StatusCode switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            414 => "URI Too Long",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            503 => "Service Unavailable",
            505 => "HTTP Version Not Supported",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Reset the response for reuse
    /// </summary>
    public void Reset()
    {
        StatusCode = 200;
        _headers?.Clear();
        Body = null;
        BodyObject = null;
        BodySerializerOptions = null;
        GzipCompressionLevel = null;
        StreamHandler = null;
        KeepAlive = true;
        _contentType = "application/json";
    }

    /// <summary>
    /// Materialize BodyObject into Body if deferred serialization is pending.
    /// Called by response writers and after middleware processing.
    /// </summary>
    public void MaterializeDeferredBody()
    {
        if (Body == null && BodyObject != null)
        {
            Body = JsonSerializer.SerializeToUtf8Bytes(BodyObject, BodyObject.GetType(), BodySerializerOptions);
            BodyObject = null;
            BodySerializerOptions = null;
        }
    }
}
