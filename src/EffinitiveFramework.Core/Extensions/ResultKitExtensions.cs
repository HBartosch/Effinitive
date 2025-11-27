using EffinitiveFramework.Core.Http;
using Routya.ResultKit;
using System.Text.Json;

namespace EffinitiveFramework.Core.Extensions;

/// <summary>
/// Extension methods for integrating Routya.ResultKit with EffinitiveFramework.
/// </summary>
public static class ResultKitExtensions
{
    /// <summary>
    /// Converts a Routya.ResultKit Result&lt;T&gt; to an EffinitiveFramework HttpResponse.
    /// </summary>
    /// <typeparam name="T">The type of data in the result.</typeparam>
    /// <param name="result">The result to convert.</param>
    /// <param name="jsonOptions">Optional JSON serialization options.</param>
    /// <returns>An HttpResponse with appropriate status code, content-type, and body.</returns>
    public static HttpResponse ToHttpResponse<T>(this Result<T> result, JsonSerializerOptions? jsonOptions = null)
    {
        var response = new HttpResponse
        {
            StatusCode = result.StatusCode
        };
        
        var options = jsonOptions ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        if (result.Success)
        {
            // Success response
            if (result.Data != null)
            {
                response.Body = JsonSerializer.SerializeToUtf8Bytes(result.Data, options);
                response.ContentType = "application/json";
            }
            else if (result.StatusCode == 204) // NoContent
            {
                response.Body = Array.Empty<byte>();
                response.ContentType = string.Empty;
            }
        }
        else
        {
            // Error response with ProblemDetails
            if (result.Error != null)
            {
                response.Body = JsonSerializer.SerializeToUtf8Bytes(result.Error, options);
                response.ContentType = "application/problem+json";
            }
        }
        
        return response;
    }
    
    /// <summary>
    /// Converts Routya.ResultKit ProblemDetails to an EffinitiveFramework HttpResponse.
    /// </summary>
    /// <param name="problemDetails">The problem details to convert.</param>
    /// <param name="jsonOptions">Optional JSON serialization options.</param>
    /// <returns>An HttpResponse with the problem details.</returns>
    public static HttpResponse ToHttpResponse(this Routya.ResultKit.ProblemDetails problemDetails, JsonSerializerOptions? jsonOptions = null)
    {
        var options = jsonOptions ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        return new HttpResponse
        {
            StatusCode = problemDetails.Status ?? 500,
            ContentType = "application/problem+json",
            Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, options)
        };
    }
}
