using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Http.ServerSentEvents;

/// <summary>
/// Delegate for SSE stream handler
/// </summary>
public delegate Task SseStreamHandler(SseStream stream, CancellationToken cancellationToken);

/// <summary>
/// Base class for Server-Sent Events endpoints with no request body
/// </summary>
public abstract class NoRequestSseEndpointBase : IEndpoint
{
    protected abstract string Method { get; }
    protected abstract string Route { get; }

    /// <summary>
    /// Handle the SSE stream
    /// </summary>
    protected abstract Task HandleStreamAsync(SseStream stream, CancellationToken cancellationToken);

    public string GetMethod() => Method;
    public string GetRoute() => Route;
    
    /// <summary>
    /// Configure the endpoint (required by IEndpoint interface)
    /// </summary>
    public void Configure()
    {
        // No additional configuration needed for SSE endpoints
    }

    /// <summary>
    /// Execute the endpoint and return an SSE streaming response
    /// </summary>
    public async Task<HttpResponse> ExecuteAsync(HttpRequest httpRequest, CancellationToken cancellationToken = default)
    {
        // Create SSE response with streaming callback
        var response = new HttpResponse
        {
            StatusCode = 200,
            ContentType = "text/event-stream",
            KeepAlive = true
        };

        // Set required SSE headers
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        // Store the stream handler for later execution
        response.StreamHandler = async (stream, ct) =>
        {
            await using var sseStream = new SseStream(stream);
            await HandleStreamAsync(sseStream, ct);
        };

        return response;
    }
}

/// <summary>
/// Base class for Server-Sent Events endpoints with request body
/// </summary>
public abstract class SseEndpointBase<TRequest> : IEndpoint where TRequest : new()
{
    protected abstract string Method { get; }
    protected abstract string Route { get; }

    /// <summary>
    /// Handle the SSE stream
    /// </summary>
    protected abstract Task HandleStreamAsync(TRequest request, SseStream stream, CancellationToken cancellationToken);

    public string GetMethod() => Method;
    public string GetRoute() => Route;
    
    /// <summary>
    /// Configure the endpoint (required by IEndpoint interface)
    /// </summary>
    public void Configure()
    {
        // No additional configuration needed for SSE endpoints
    }

    /// <summary>
    /// Execute the endpoint and return an SSE streaming response
    /// </summary>
    public async Task<HttpResponse> ExecuteAsync(HttpRequest httpRequest, CancellationToken cancellationToken = default)
    {
        // Deserialize request (if needed)
        TRequest request;
        if (typeof(TRequest) == typeof(EmptyRequest))
        {
            request = new TRequest();
        }
        else
        {
            // For non-empty requests, deserialize from JSON
            var requestBody = httpRequest.Body ?? Array.Empty<byte>();
            if (requestBody.Length > 0)
            {
                request = await System.Text.Json.JsonSerializer.DeserializeAsync<TRequest>(
                    new MemoryStream(requestBody), 
                    cancellationToken: cancellationToken) ?? new TRequest();
            }
            else
            {
                request = new TRequest();
            }
        }

        // Create SSE response with streaming callback
        var response = new HttpResponse
        {
            StatusCode = 200,
            ContentType = "text/event-stream",
            KeepAlive = true
        };

        // Set required SSE headers
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        // Store the stream handler for later execution
        response.StreamHandler = async (stream, ct) =>
        {
            await using var sseStream = new SseStream(stream);
            await HandleStreamAsync(request, sseStream, ct);
        };

        return response;
    }
}

/// <summary>
/// Base class for Server-Sent Events endpoints with request and response types
/// Provides type-safe streaming with strongly-typed event data
/// </summary>
/// <typeparam name="TRequest">Request payload type</typeparam>
/// <typeparam name="TEventData">Event data type for type-safe streaming</typeparam>
public abstract class SseEndpointBase<TRequest, TEventData> : IEndpoint where TRequest : new()
{
    protected abstract string Method { get; }
    protected abstract string Route { get; }

    /// <summary>
    /// Handle the SSE stream with strongly-typed event writer
    /// </summary>
    protected abstract Task HandleStreamAsync(TRequest request, TypedSseStream<TEventData> stream, CancellationToken cancellationToken);

    public string GetMethod() => Method;
    public string GetRoute() => Route;
    
    /// <summary>
    /// Configure the endpoint (required by IEndpoint interface)
    /// </summary>
    public void Configure()
    {
        // No additional configuration needed for SSE endpoints
    }

    /// <summary>
    /// Execute the endpoint and return an SSE streaming response
    /// </summary>
    public async Task<HttpResponse> ExecuteAsync(HttpRequest httpRequest, CancellationToken cancellationToken = default)
    {
        // Deserialize request (if needed)
        TRequest request;
        if (typeof(TRequest) == typeof(EmptyRequest))
        {
            request = new TRequest();
        }
        else
        {
            // For non-empty requests, deserialize from JSON
            var requestBody = httpRequest.Body ?? Array.Empty<byte>();
            if (requestBody.Length > 0)
            {
                request = await System.Text.Json.JsonSerializer.DeserializeAsync<TRequest>(
                    new MemoryStream(requestBody), 
                    cancellationToken: cancellationToken) ?? new TRequest();
            }
            else
            {
                request = new TRequest();
            }
        }

        // Create SSE response with streaming callback
        var response = new HttpResponse
        {
            StatusCode = 200,
            ContentType = "text/event-stream",
            KeepAlive = true
        };

        // Set required SSE headers
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        // Store the stream handler for later execution
        response.StreamHandler = async (stream, ct) =>
        {
            await using var sseStream = new SseStream(stream);
            var typedStream = new TypedSseStream<TEventData>(sseStream);
            await HandleStreamAsync(request, typedStream, ct);
        };

        return response;
    }
}
