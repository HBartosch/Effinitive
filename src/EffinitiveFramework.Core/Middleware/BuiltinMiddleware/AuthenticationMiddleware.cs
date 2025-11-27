using System.Reflection;
using EffinitiveFramework.Core.Authentication;
using EffinitiveFramework.Core.Authorization;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Middleware.BuiltinMiddleware;

/// <summary>
/// Options for authentication middleware
/// </summary>
public sealed class AuthenticationMiddlewareOptions
{
    /// <summary>
    /// The authentication handler to use
    /// </summary>
    public IAuthenticationHandler Handler { get; set; } = null!;

    /// <summary>
    /// Whether to require authentication by default for all endpoints
    /// </summary>
    public bool RequireAuthenticationByDefault { get; set; } = false;

    /// <summary>
    /// Whether to return 401 (Unauthorized) or 403 (Forbidden) on authorization failure
    /// - 401: User is not authenticated
    /// - 403: User is authenticated but doesn't have required permissions
    /// </summary>
    public bool Return403OnFailure { get; set; } = false;
}

/// <summary>
/// Middleware that handles authentication and authorization
/// </summary>
public sealed class AuthenticationMiddleware : IMiddleware
{
    private readonly AuthenticationMiddlewareOptions _options;

    public AuthenticationMiddleware(AuthenticationMiddlewareOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.Handler == null)
            throw new ArgumentException("Handler is required for authentication middleware", nameof(options));
    }

    public async ValueTask<HttpResponse> InvokeAsync(HttpRequest request, RequestDelegate next, CancellationToken cancellationToken)
    {
        // Get endpoint metadata from request.Items (set by router)
        Type? endpointType = null;
        if (request.Items?.TryGetValue("EndpointType", out var endpointTypeObj) == true)
        {
            endpointType = endpointTypeObj as Type;
        }

        bool requiresAuth = _options.RequireAuthenticationByDefault;
        bool allowAnonymous = false;
        string[]? requiredRoles = null;
        string? requiredPolicy = null;

        // Check for authorization attributes on endpoint
        if (endpointType != null)
        {
            // Check for [AllowAnonymous]
            allowAnonymous = endpointType.GetCustomAttribute<AllowAnonymousAttribute>() != null;

            // Check for [Authorize]
            var authorizeAttr = endpointType.GetCustomAttribute<AuthorizeAttribute>();
            if (authorizeAttr != null)
            {
                requiresAuth = true;
                requiredRoles = authorizeAttr.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => r.Trim())
                    .ToArray();
                requiredPolicy = authorizeAttr.Policy;
            }
        }

        // If endpoint allows anonymous access, skip authentication
        if (allowAnonymous)
        {
            request.User = new ClaimsPrincipal(); // Anonymous user
            return await next(request, cancellationToken);
        }

        // Authenticate the request if required
        if (requiresAuth)
        {
            var authResult = await _options.Handler.AuthenticateAsync(request, cancellationToken);

            if (!authResult.Succeeded)
            {
                // Return 401 Unauthorized
                return new HttpResponse
                {
                    StatusCode = 401,
                    Body = System.Text.Encoding.UTF8.GetBytes(authResult.FailureReason ?? "Unauthorized"),
                    ContentType = "text/plain",
                    Headers = { ["WWW-Authenticate"] = "Bearer" }
                };
            }

            request.User = authResult.Principal;

            // Check role-based authorization
            if (requiredRoles != null && requiredRoles.Length > 0)
            {
                if (!authResult.Principal!.IsInAnyRole(requiredRoles))
                {
                    var statusCode = _options.Return403OnFailure ? 403 : 401;
                    var message = statusCode == 403 
                        ? $"Forbidden: User does not have required role(s): {string.Join(", ", requiredRoles)}"
                        : "Unauthorized: Insufficient permissions";

                    return new HttpResponse
                    {
                        StatusCode = statusCode,
                        Body = System.Text.Encoding.UTF8.GetBytes(message),
                        ContentType = "text/plain"
                    };
                }
            }

            // Policy-based authorization could be implemented here
            // For now, we just note that a policy was specified but not enforced
            if (!string.IsNullOrWhiteSpace(requiredPolicy))
            {
                // TODO: Implement policy-based authorization
                // For now, just log a warning
                Console.WriteLine($"Warning: Policy '{requiredPolicy}' specified but policy authorization not yet implemented");
            }
        }
        else
        {
            // Try to authenticate anyway (optional authentication)
            var authResult = await _options.Handler.AuthenticateAsync(request, cancellationToken);
            request.User = authResult.Succeeded ? authResult.Principal : new ClaimsPrincipal();
        }

        // Call next middleware
        return await next(request, cancellationToken);
    }
}
