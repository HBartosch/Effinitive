namespace EffinitiveFramework.Core;

/// <summary>
/// Base interface for all endpoints in the framework
/// </summary>
public interface IEndpoint
{
    /// <summary>
    /// Configure the endpoint route and HTTP method
    /// </summary>
    void Configure();
}

/// <summary>
/// Generic endpoint with request and response types (using ValueTask for sync/cached operations)
/// </summary>
/// <typeparam name="TRequest">Request payload type</typeparam>
/// <typeparam name="TResponse">Response payload type</typeparam>
public interface IEndpoint<TRequest, TResponse> : IEndpoint
{
    /// <summary>
    /// Handle the endpoint request (ValueTask for minimal allocations in sync scenarios)
    /// </summary>
    ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic endpoint with request and response types (using Task for true async I/O operations)
/// </summary>
/// <typeparam name="TRequest">Request payload type</typeparam>
/// <typeparam name="TResponse">Response payload type</typeparam>
public interface IAsyncEndpoint<TRequest, TResponse> : IEndpoint
{
    /// <summary>
    /// Handle the endpoint request (Task for true async I/O like database operations)
    /// </summary>
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}
