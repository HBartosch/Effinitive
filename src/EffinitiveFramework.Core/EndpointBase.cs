using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core;

/// <summary>
/// Base class for endpoints with synchronous or cached operations (uses ValueTask for minimal allocations)
/// Use this for: in-memory operations, cached data, simple transformations
/// </summary>
/// <typeparam name="TRequest">Request type</typeparam>
/// <typeparam name="TResponse">Response type</typeparam>
public abstract class EndpointBase<TRequest, TResponse> : IEndpoint<TRequest, TResponse>
{
    /// <summary>
    /// Gets the current HTTP request context (available during request handling)
    /// </summary>
    public HttpRequest? HttpContext { get; internal set; }
    
    /// <summary>
    /// HTTP method for this endpoint (GET, POST, etc.)
    /// </summary>
    protected abstract string Method { get; }
    
    /// <summary>
    /// Route pattern for this endpoint
    /// </summary>
    protected abstract string Route { get; }

    /// <summary>
    /// Handle the endpoint request using ValueTask (optimal for synchronous/cached operations)
    /// </summary>
    public abstract ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configure the endpoint route
    /// </summary>
    public virtual void Configure()
    {
        // To be implemented during endpoint registration
    }
}

/// <summary>
/// Base class for endpoints with true I/O operations (uses Task for proper async handling)
/// Use this for: database calls, external API calls, file I/O, message queue operations
/// </summary>
/// <typeparam name="TRequest">Request type</typeparam>
/// <typeparam name="TResponse">Response type</typeparam>
public abstract class AsyncEndpointBase<TRequest, TResponse> : IAsyncEndpoint<TRequest, TResponse>
{
    /// <summary>
    /// Gets the current HTTP request context (available during request handling)
    /// </summary>
    public HttpRequest? HttpContext { get; internal set; }
    
    /// <summary>
    /// HTTP method for this endpoint (GET, POST, etc.)
    /// </summary>
    protected abstract string Method { get; }
    
    /// <summary>
    /// Route pattern for this endpoint
    /// </summary>
    protected abstract string Route { get; }

    /// <summary>
    /// Handle the endpoint request using Task (optimal for true async I/O operations)
    /// </summary>
    public abstract Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configure the endpoint route
    /// </summary>
    public virtual void Configure()
    {
        // To be implemented during endpoint registration
    }
}
