using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;
using Routya.ResultKit;
using System.Text.Json;

namespace EffinitiveFramework.Core.Middleware.Builtin;

/// <summary>
/// Middleware that automatically validates request models using RoutyaResultKit's .Validate() extension.
/// Only validates if the request object implements validation attributes.
/// </summary>
public sealed class ValidationMiddleware : MiddlewareBase
{
    public override async ValueTask<HttpResponse> InvokeAsync(
        HttpRequest request,
        RequestDelegate next,
        CancellationToken cancellationToken)
    {
        // Get the request body object if it exists (set during deserialization)
        if (request.Items != null && request.Items.TryGetValue("RequestBody", out var requestBody))
        {
            // Use reflection to call .Validate() extension method
            var validateMethod = typeof(ValidationExtensions).GetMethod("Validate");
            if (validateMethod != null)
            {
                var genericMethod = validateMethod.MakeGenericMethod(requestBody.GetType());
                var result = genericMethod.Invoke(null, new[] { requestBody });
                
                // Check if validation failed
                var successProperty = result?.GetType().GetProperty("Success");
                var success = (bool)(successProperty?.GetValue(result) ?? true);
                
                if (!success)
                {
                    // Get the Error property (ProblemDetails)
                    var errorProperty = result?.GetType().GetProperty("Error");
                    var problemDetails = errorProperty?.GetValue(result) as Routya.ResultKit.ProblemDetails;
                    
                    if (problemDetails != null)
                    {
                        // Return validation error response
                        var response = new HttpResponse
                        {
                            StatusCode = problemDetails.Status ?? 400,
                            ContentType = "application/problem+json"
                        };
                        
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        });
                        
                        return response;
                    }
                }
            }
        }
        
        // Validation passed or no validation needed, continue to next middleware/endpoint
        return await next(request, cancellationToken);
    }
}
