using EffinitiveFramework.Core.Middleware.BuiltinMiddleware;

namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Extension methods for configuring authentication
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Add JWT authentication to the application
    /// </summary>
    public static EffinitiveAppBuilder UseJwtAuthentication(
        this EffinitiveAppBuilder builder,
        Action<JwtAuthenticationOptions> configure,
        bool requireByDefault = false)
    {
        var options = new JwtAuthenticationOptions();
        configure(options);

        var handler = new JwtAuthenticationHandler(options);

        var middlewareOptions = new AuthenticationMiddlewareOptions
        {
            Handler = handler,
            RequireAuthenticationByDefault = requireByDefault
        };

        return builder.Use((request, next, ct) =>
        {
            var middleware = new AuthenticationMiddleware(middlewareOptions);
            return middleware.InvokeAsync(request, next, ct);
        });
    }

    /// <summary>
    /// Add API key authentication to the application
    /// </summary>
    public static EffinitiveAppBuilder UseApiKeyAuthentication(
        this EffinitiveAppBuilder builder,
        ApiKeyValidator validator,
        Action<ApiKeyAuthenticationOptions>? configure = null,
        bool requireByDefault = false)
    {
        var options = new ApiKeyAuthenticationOptions
        {
            Validator = validator
        };

        configure?.Invoke(options);

        var handler = new ApiKeyAuthenticationHandler(options);

        var middlewareOptions = new AuthenticationMiddlewareOptions
        {
            Handler = handler,
            RequireAuthenticationByDefault = requireByDefault
        };

        return builder.Use((request, next, ct) =>
        {
            var middleware = new AuthenticationMiddleware(middlewareOptions);
            return middleware.InvokeAsync(request, next, ct);
        });
    }

    /// <summary>
    /// Add custom authentication to the application
    /// </summary>
    public static EffinitiveAppBuilder UseCustomAuthentication<THandler>(
        this EffinitiveAppBuilder builder,
        bool requireByDefault = false)
        where THandler : IAuthenticationHandler, new()
    {
        var handler = new THandler();

        var middlewareOptions = new AuthenticationMiddlewareOptions
        {
            Handler = handler,
            RequireAuthenticationByDefault = requireByDefault
        };

        return builder.Use((request, next, ct) =>
        {
            var middleware = new AuthenticationMiddleware(middlewareOptions);
            return middleware.InvokeAsync(request, next, ct);
        });
    }

    /// <summary>
    /// Add custom authentication with instance to the application
    /// </summary>
    public static EffinitiveAppBuilder UseCustomAuthentication(
        this EffinitiveAppBuilder builder,
        IAuthenticationHandler handler,
        bool requireByDefault = false)
    {
        var middlewareOptions = new AuthenticationMiddlewareOptions
        {
            Handler = handler,
            RequireAuthenticationByDefault = requireByDefault
        };

        return builder.Use((request, next, ct) =>
        {
            var middleware = new AuthenticationMiddleware(middlewareOptions);
            return middleware.InvokeAsync(request, next, ct);
        });
    }
}
