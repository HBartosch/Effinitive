using System.Runtime.CompilerServices;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Middleware;

/// <summary>
/// High-performance middleware pipeline with minimal allocation
/// </summary>
public sealed class MiddlewarePipeline
{
    private readonly List<Func<RequestDelegate, RequestDelegate>> _middlewareFactories = new();
    private RequestDelegate? _pipeline;
    private readonly IServiceProvider? _serviceProvider;

    public MiddlewarePipeline(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Add middleware using a factory function
    /// </summary>
    public void Use(Func<RequestDelegate, RequestDelegate> middleware)
    {
        _middlewareFactories.Add(middleware);
        _pipeline = null; // Invalidate cached pipeline
    }

    /// <summary>
    /// Add middleware instance (will be created per request if it has dependencies)
    /// </summary>
    public void Use<TMiddleware>() where TMiddleware : IMiddleware
    {
        Use(next => async (request, cancellationToken) =>
        {
            // Create middleware instance with DI support
            var middleware = CreateMiddleware<TMiddleware>();
            return await middleware.InvokeAsync(request, next, cancellationToken);
        });
    }

    /// <summary>
    /// Add middleware with inline lambda
    /// </summary>
    public void Use(Func<HttpRequest, RequestDelegate, CancellationToken, ValueTask<HttpResponse>> middleware)
    {
        Use(next => (request, cancellationToken) => middleware(request, next, cancellationToken));
    }

    /// <summary>
    /// Build the pipeline (called once, cached for performance)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RequestDelegate Build(RequestDelegate finalHandler)
    {
        // NOTE: We don't cache the final pipeline because the finalHandler changes per request
        // We only cache at the middleware factory level
        
        // Build pipeline in reverse order (last middleware added executes first)
        var pipeline = finalHandler;
        
        for (int i = _middlewareFactories.Count - 1; i >= 0; i--)
        {
            pipeline = _middlewareFactories[i](pipeline);
        }

        return pipeline;
    }

    /// <summary>
    /// Execute the pipeline
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpResponse> ExecuteAsync(HttpRequest request, RequestDelegate handler, CancellationToken cancellationToken)
    {
        var pipeline = Build(handler);
        return pipeline(request, cancellationToken);
    }

    private TMiddleware CreateMiddleware<TMiddleware>() where TMiddleware : IMiddleware
    {
        if (_serviceProvider != null)
        {
            // Try to get from DI container
            var instance = _serviceProvider.GetService(typeof(TMiddleware));
            if (instance != null)
                return (TMiddleware)instance;
        }

        // Fallback to Activator
        return Activator.CreateInstance<TMiddleware>();
    }
}
