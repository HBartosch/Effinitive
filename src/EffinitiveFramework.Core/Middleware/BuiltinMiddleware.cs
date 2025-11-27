using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;
using System.Diagnostics;

namespace EffinitiveFramework.Core.Middleware.Builtin;

/// <summary>
/// Logging middleware - logs request details and timing
/// </summary>
public sealed class LoggingMiddleware : MiddlewareBase
{
    private readonly ILogger? _logger;

    public LoggingMiddleware(ILogger? logger = null)
    {
        _logger = logger;
    }

    public override async ValueTask<HttpResponse> InvokeAsync(
        HttpRequest request, 
        RequestDelegate next, 
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        
        _logger?.LogInformation($"→ {request.Method} {request.Path}");

        var response = await next(request, cancellationToken);

        sw.Stop();
        _logger?.LogInformation($"← {request.Method} {request.Path} - {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");

        return response;
    }
}

/// <summary>
/// CORS middleware - handles Cross-Origin Resource Sharing
/// </summary>
public sealed class CorsMiddleware : MiddlewareBase
{
    private readonly string _allowedOrigins;
    private readonly string _allowedMethods;
    private readonly string _allowedHeaders;

    public CorsMiddleware(
        string allowedOrigins = "*",
        string allowedMethods = "GET, POST, PUT, DELETE, OPTIONS",
        string allowedHeaders = "*")
    {
        _allowedOrigins = allowedOrigins;
        _allowedMethods = allowedMethods;
        _allowedHeaders = allowedHeaders;
    }

    public override async ValueTask<HttpResponse> InvokeAsync(
        HttpRequest request, 
        RequestDelegate next, 
        CancellationToken cancellationToken)
    {
        // Handle preflight requests
        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflightResponse = new HttpResponse
            {
                StatusCode = 204
            };
            preflightResponse.Headers["Access-Control-Allow-Origin"] = _allowedOrigins;
            preflightResponse.Headers["Access-Control-Allow-Methods"] = _allowedMethods;
            preflightResponse.Headers["Access-Control-Allow-Headers"] = _allowedHeaders;
            preflightResponse.Headers["Access-Control-Max-Age"] = "86400";
            return preflightResponse;
        }

        var response = await next(request, cancellationToken);

        // Add CORS headers to response
        response.Headers["Access-Control-Allow-Origin"] = _allowedOrigins;

        return response;
    }
}

/// <summary>
/// Exception handling middleware - catches and handles exceptions
/// </summary>
public sealed class ExceptionHandlerMiddleware : MiddlewareBase
{
    private readonly ILogger? _logger;
    private readonly bool _includeDetails;

    public ExceptionHandlerMiddleware(ILogger? logger = null, bool includeDetails = false)
    {
        _logger = logger;
        _includeDetails = includeDetails;
    }

    public override async ValueTask<HttpResponse> InvokeAsync(
        HttpRequest request, 
        RequestDelegate next, 
        CancellationToken cancellationToken)
    {
        try
        {
            return await next(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Unhandled exception: {ex.Message}");
            _logger?.LogError(ex.StackTrace ?? "No stack trace available");

            var errorResponse = new
            {
                error = "Internal Server Error",
                message = _includeDetails ? ex.Message : "An error occurred processing your request",
                path = request.Path
            };

            var response = new HttpResponse
            {
                StatusCode = 500,
                Body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(errorResponse)
            };
            response.Headers["Content-Type"] = "application/json";
            return response;
        }
    }
}

/// <summary>
/// Simple logger interface (replace with your favorite logger)
/// </summary>
public interface ILogger
{
    void LogInformation(string message);
    void LogError(string message);
}

/// <summary>
/// Console logger implementation
/// </summary>
public sealed class ConsoleLogger : ILogger
{
    public void LogInformation(string message)
    {
        Console.WriteLine($"[INFO] {DateTime.UtcNow:HH:mm:ss} {message}");
    }

    public void LogError(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss} {message}");
        Console.ForegroundColor = originalColor;
    }
}
