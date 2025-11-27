namespace EffinitiveFramework.Core.Http;

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
        KeepAlive = true;
    }
}
