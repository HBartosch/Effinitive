using System.Diagnostics;
using System.Text.Json.Serialization;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// RFC 7807 Problem Details for HTTP APIs
/// </summary>
public sealed class ProblemDetails
{
    /// <summary>
    /// A URI reference that identifies the problem type
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "about:blank";

    /// <summary>
    /// A short, human-readable summary of the problem type
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The HTTP status code
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>
    /// A human-readable explanation specific to this occurrence of the problem
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>
    /// A URI reference that identifies the specific occurrence of the problem
    /// </summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; set; }

    /// <summary>
    /// Stack trace (only included in development environment)
    /// </summary>
    [JsonPropertyName("stackTrace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; set; }

    /// <summary>
    /// Create a ProblemDetails for an exception
    /// </summary>
    public static ProblemDetails FromException(Exception exception, int statusCode = 500, string? instance = null)
    {
        var isDevelopment = IsDevelopmentEnvironment();

        return new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            Title = GetTitleForStatusCode(statusCode),
            Status = statusCode,
            Detail = isDevelopment ? exception.Message : "An error occurred while processing your request.",
            Instance = instance,
            StackTrace = isDevelopment ? exception.StackTrace : null
        };
    }

    /// <summary>
    /// Create a ProblemDetails for a status code
    /// </summary>
    public static ProblemDetails ForStatusCode(int statusCode, string? detail = null, string? instance = null)
    {
        return new ProblemDetails
        {
            Type = GetTypeForStatusCode(statusCode),
            Title = GetTitleForStatusCode(statusCode),
            Status = statusCode,
            Detail = detail,
            Instance = instance
        };
    }

    private static bool IsDevelopmentEnvironment()
    {
        // Check if debugger is attached
        if (Debugger.IsAttached)
            return true;

        // Check ASPNETCORE_ENVIRONMENT variable
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return environment?.Equals("Development", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetTypeForStatusCode(int statusCode)
    {
        return statusCode switch
        {
            400 => "https://tools.ietf.org/html/rfc7231#section-6.5.1",
            401 => "https://tools.ietf.org/html/rfc7235#section-3.1",
            403 => "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            404 => "https://tools.ietf.org/html/rfc7231#section-6.5.4",
            405 => "https://tools.ietf.org/html/rfc7231#section-6.5.5",
            500 => "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            503 => "https://tools.ietf.org/html/rfc7231#section-6.6.4",
            _ => "about:blank"
        };
    }

    private static string GetTitleForStatusCode(int statusCode)
    {
        return statusCode switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Error"
        };
    }
}
