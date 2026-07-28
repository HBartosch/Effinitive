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
    /// A stream whose contents form the response body, with a known length.
    /// Lets the server stream a payload (e.g. a file on disk) to the client without
    /// buffering the whole thing in memory. Exactly <see cref="BodyStreamLength"/> bytes
    /// are copied, after which the stream is disposed by the writer.
    /// When set, this takes precedence over <see cref="Body"/>.
    /// </summary>
    public Stream? BodyStream { get; set; }

    /// <summary>
    /// Number of bytes to send from <see cref="BodyStream"/>. Used as the Content-Length.
    /// </summary>
    public long BodyStreamLength { get; set; }

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
            206 => "Partial Content",
            304 => "Not Modified",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            414 => "URI Too Long",
            416 => "Range Not Satisfiable",
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
        // Dispose an unconsumed body stream so file handles aren't leaked on error paths.
        if (BodyStream != null)
        {
            BodyStream.Dispose();
            BodyStream = null;
        }
        BodyStreamLength = 0;
        BodyObject = null;
        BodySerializerOptions = null;
        GzipCompressionLevel = null;
        StreamHandler = null;
        KeepAlive = true;
        _contentType = "application/json";
    }

    /// <summary>
    /// Reads <see cref="BodyStream"/> fully into <see cref="Body"/> (bounded by
    /// <see cref="BodyStreamLength"/>) and disposes the stream. Used by transports that frame
    /// the body from a byte[] (HTTP/2, HTTP/3) rather than streaming it to a PipeWriter.
    /// No-op when no stream body is set.
    /// </summary>
    public async ValueTask MaterializeBodyStreamAsync(CancellationToken cancellationToken = default)
    {
        if (BodyStream == null)
            return;

        var stream = BodyStream;
        var length = (int)BodyStreamLength;
        try
        {
            var buffer = new byte[length];
            var total = 0;
            while (total < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, length - total), cancellationToken);
                if (read <= 0) break;
                total += read;
            }
            Body = total == length ? buffer : buffer[..total];
        }
        finally
        {
            await stream.DisposeAsync();
            BodyStream = null;
            BodyStreamLength = 0;
        }
    }

    /// <summary>
    /// Adds a field name to the <c>Vary</c> header, preserving names already present.
    /// Several concerns select the representation independently — compression varies by
    /// <c>Accept-Encoding</c> while a cached endpoint may vary by <c>Accept-Language</c> — and a plain
    /// assignment from whichever runs last would silently drop the others, letting a shared cache serve
    /// the wrong representation.
    /// </summary>
    public void AppendVary(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
            return;

        if (!Headers.TryGetValue("Vary", out var existing) || string.IsNullOrEmpty(existing))
        {
            Headers["Vary"] = headerName;
            return;
        }

        // "Vary: *" already means "varies by everything" — narrowing it would be wrong.
        if (existing.AsSpan().Trim().SequenceEqual("*"))
            return;

        foreach (var field in existing.Split(','))
        {
            if (field.AsSpan().Trim().Equals(headerName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return;
        }

        Headers["Vary"] = string.Concat(existing, ", ", headerName);
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
