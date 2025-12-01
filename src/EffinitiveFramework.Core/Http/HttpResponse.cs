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
    /// <summary>
    /// HTTP status code (200, 404, 500, etc.)
    /// </summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>
    /// Response headers (name -> value)
    /// </summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Response body as byte array
    /// </summary>
    public byte[]? Body { get; set; }

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
        get => Headers.TryGetValue("Content-Type", out var value) ? value : "application/json";
        set => Headers["Content-Type"] = value;
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
            500 => "Internal Server Error",
            501 => "Not Implemented",
            503 => "Service Unavailable",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Reset the response for reuse
    /// </summary>
    public void Reset()
    {
        StatusCode = 200;
        Headers.Clear();
        Body = null;
        StreamHandler = null;
        KeepAlive = true;
    }
}
