using System.Text.Json;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;

namespace EffinitiveFramework.Core;

public sealed partial class EffinitiveServer
{
    private static object? ConvertRouteParam(string value, Type targetType)
    {
        // Handle nullable value types by converting using the underlying type
        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var isNullable = nonNullableType != targetType;

        try
        {
            if (nonNullableType == typeof(string)) return value;
            if (nonNullableType == typeof(int))    return int.Parse(value);
            if (nonNullableType == typeof(long))   return long.Parse(value);
            if (nonNullableType == typeof(Guid))   return Guid.Parse(value);

            return Convert.ChangeType(value, nonNullableType);
        }
        catch
        {
            if (isNullable)
            {
                // For nullable targets, treat conversion failures as null values
                return null;
            }

            // Preserve existing behavior for non-nullable types
            throw;
        }
    }

    /// <summary>
    /// RFC 9110 §8.8.3.2 Weak comparison: two entity-tags are equivalent
    /// if their opaque-tags match character-by-character, regardless of W/ prefix.
    /// </summary>
    internal static bool WeakETagMatch(string ifNoneMatch, string responseEtag)
    {
        var inm = ifNoneMatch.AsSpan().Trim();

        // Wildcard matches any existing resource
        if (inm is "*")
            return true;

        // Get opaque tag from response ETag (strip W/ prefix)
        var responseOpaque = responseEtag.AsSpan().Trim();
        if (responseOpaque.StartsWith("W/", StringComparison.Ordinal))
            responseOpaque = responseOpaque[2..];

        // Must be a quoted string to be valid
        if (responseOpaque.Length < 2 || responseOpaque[0] != '"' || responseOpaque[^1] != '"')
            return false;

        // Parse comma-separated ETags in If-None-Match
        foreach (var segment in ifNoneMatch.Split(','))
        {
            var candidate = segment.AsSpan().Trim();
            if (candidate.Length == 0) continue;

            // Strip W/ prefix for weak comparison
            if (candidate.StartsWith("W/", StringComparison.Ordinal))
                candidate = candidate[2..];

            // Must be a properly quoted ETag to match
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
            {
                if (candidate.SequenceEqual(responseOpaque))
                    return true;
            }
        }

        return false;
    }

    private void SerializeResponse(HttpResponse response, object? responseObj, string contentType)
    {
        if (responseObj is Http.RawResponse raw)
        {
            response.StatusCode = raw.StatusCode;
            response.ContentType = raw.ContentType;
            response.Body = raw.Body;
            if (raw.Headers != null)
            {
                foreach (var h in raw.Headers)
                    response.Headers[h.Key] = h.Value;
            }
            return;
        }

        if (responseObj != null)
        {
            response.StatusCode = 200;
            response.ContentType = contentType;

            if (contentType == "text/plain")
            {
                response.Body = responseObj is string str
                    ? System.Text.Encoding.UTF8.GetBytes(str)
                    : System.Text.Encoding.UTF8.GetBytes(responseObj.ToString() ?? "");
            }
            else
            {
                response.Body = JsonSerializer.SerializeToUtf8Bytes(responseObj, _options.JsonOptions);
            }
        }
        else
        {
            response.StatusCode = 200;
            response.Body = Array.Empty<byte>();
            response.ContentType = "text/plain";
        }
    }

    private async Task HandleErrorAsync(Exception exception, HttpRequest request, HttpResponse response)
    {
        response.StatusCode = 500;
        var problemDetails = ProblemDetails.FromException(exception, 500, request.Path);
        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
        response.ContentType = "application/problem+json";

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle HTTP/2 connection
    /// </summary>
    private async Task HandleHttp2ConnectionAsync(HttpConnection connection, CancellationToken cancellationToken)
    {
        // Create request handler that routes through the framework
        async Task<HttpResponse> RequestHandler(HttpRequest request)
        {
            var response = new HttpResponse();
            
            try
            {
                await HandleRequestAsync(request, response, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync(ex, request, response);
            }
            
            return response;
        }
        
        var http2Connection = new Http2Connection(connection.Stream!, RequestHandler);
        
        try
        {
            await http2Connection.ProcessAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (!_isProduction)
                Console.WriteLine($"HTTP/2 connection error: {ex.Message}");
        }
        finally
        {
            await http2Connection.DisposeAsync();
        }
    }
}

/// <summary>
/// No-op service provider for when DI is not configured
/// </summary>
internal sealed class NoOpServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
