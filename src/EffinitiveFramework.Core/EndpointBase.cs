using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core;

/// <summary>
/// Base class for endpoints without a request body (e.g., GET endpoints)
/// Use this for: simple GET endpoints, health checks, status endpoints
/// </summary>
/// <typeparam name="TResponse">Response type</typeparam>
public abstract class NoRequestEndpointBase<TResponse> : IEndpoint<EmptyRequest, TResponse>
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
    /// Content type for the response (defaults to application/json)
    /// Override this to return different content types (e.g., "text/plain", "text/html")
    /// </summary>
    protected virtual string ContentType => "application/json";

    /// <summary>
    /// Handle the endpoint request without a request body
    /// </summary>
    public abstract ValueTask<TResponse> HandleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Internal adapter for IEndpoint interface
    /// </summary>
    ValueTask<TResponse> IEndpoint<EmptyRequest, TResponse>.HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
        => HandleAsync(cancellationToken);

    /// <summary>
    /// Configure the endpoint route
    /// </summary>
    public virtual void Configure()
    {
        // To be implemented during endpoint registration
    }
}

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
    /// Content type for the response (defaults to application/json)
    /// Override this to return different content types (e.g., "text/plain", "text/html")
    /// </summary>
    protected virtual string ContentType => "application/json";

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
    /// Content type for the response (defaults to application/json)
    /// Override this to return different content types (e.g., "text/plain", "text/html")
    /// </summary>
    protected virtual string ContentType => "application/json";

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

/// <summary>
/// Base class for async endpoints without a request body (e.g., GET endpoints with I/O)
/// Use this for: database queries without input, external API calls, file reads
/// </summary>
/// <typeparam name="TResponse">Response type</typeparam>
public abstract class NoRequestAsyncEndpointBase<TResponse> : IAsyncEndpoint<EmptyRequest, TResponse>
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
    /// Content type for the response (defaults to application/json)
    /// Override this to return different content types (e.g., "text/plain", "text/html")
    /// </summary>
    protected virtual string ContentType => "application/json";

    /// <summary>
    /// Handle the endpoint request without a request body using Task (optimal for true async I/O operations)
    /// </summary>
    public abstract Task<TResponse> HandleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Internal adapter for IAsyncEndpoint interface
    /// </summary>
    Task<TResponse> IAsyncEndpoint<EmptyRequest, TResponse>.HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
        => HandleAsync(cancellationToken);

    /// <summary>
    /// Configure the endpoint route
    /// </summary>
    public virtual void Configure()
    {
        // To be implemented during endpoint registration
    }
}
