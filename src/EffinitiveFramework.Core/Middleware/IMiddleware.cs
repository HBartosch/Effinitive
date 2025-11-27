using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Middleware;

/// <summary>
/// Middleware delegate for request processing
/// </summary>
public delegate ValueTask<HttpResponse> RequestDelegate(HttpRequest request, CancellationToken cancellationToken);

/// <summary>
/// Interface for middleware components
/// </summary>
public interface IMiddleware
{
    /// <summary>
    /// Process the request and optionally call the next middleware
    /// </summary>
    ValueTask<HttpResponse> InvokeAsync(HttpRequest request, RequestDelegate next, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for middleware with dependency injection support
/// </summary>
public abstract class MiddlewareBase : IMiddleware
{
    /// <summary>
    /// Process the request
    /// </summary>
    public abstract ValueTask<HttpResponse> InvokeAsync(HttpRequest request, RequestDelegate next, CancellationToken cancellationToken);
}
