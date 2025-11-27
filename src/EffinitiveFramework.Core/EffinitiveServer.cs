using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.ObjectPool;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;
using EffinitiveFramework.Core.Middleware;
using System.Text.Json;
using RoutyaProblemDetails = Routya.ResultKit.ProblemDetails;

namespace EffinitiveFramework.Core;

/// <summary>
/// High-performance HTTP server with TLS support
/// </summary>
public sealed class EffinitiveServer : IDisposable
{
    private readonly ServerOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly ObjectPool<HttpConnection> _connectionPool;
    private readonly SemaphoreSlim _connectionLimit;
    private readonly Router _router;
    private readonly IServiceProvider? _serviceProvider;
    private readonly MiddlewarePipeline? _middlewarePipeline;
    private readonly CancellationTokenSource _shutdownCts = new();
    
    private Socket? _httpListener;
    private Socket? _httpsListener;
    private Task? _httpAcceptTask;
    private Task? _httpsAcceptTask;

    public ServerMetrics Metrics => _metrics;

    public EffinitiveServer(
        ServerOptions options, 
        Router router, 
        IServiceProvider? serviceProvider = null,
        MiddlewarePipeline? middlewarePipeline = null)
    {
        _options = options;
        _router = router;
        _serviceProvider = serviceProvider;
        _middlewarePipeline = middlewarePipeline;
        _metrics = new ServerMetrics();
        _connectionLimit = new SemaphoreSlim(_options.MaxConcurrentConnections);
        _connectionPool = new DefaultObjectPool<HttpConnection>(
            new HttpConnectionPoolPolicy(),
            maximumRetained: _options.MaxConcurrentConnections);
    }

    /// <summary>
    /// Start the server
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Load TLS certificate if configured
        _options.TlsOptions.LoadCertificate();

        // Start HTTP listener
        if (_options.HttpPort > 0)
        {
            _httpListener = CreateListener(_options.HttpPort);
            _httpAcceptTask = AcceptConnectionsAsync(_httpListener, isSecure: false, _shutdownCts.Token);
        }

        // Start HTTPS listener
        if (_options.HttpsPort > 0 && _options.TlsOptions.Certificate != null)
        {
            _httpsListener = CreateListener(_options.HttpsPort);
            _httpsAcceptTask = AcceptConnectionsAsync(_httpsListener, isSecure: true, _shutdownCts.Token);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stop the server gracefully
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);

        // Signal shutdown
        _shutdownCts.Cancel();

        // Stop accepting new connections
        _httpListener?.Close();
        _httpsListener?.Close();

        // Wait for active connections to complete (with timeout)
        var shutdownTask = Task.WhenAll(
            _httpAcceptTask ?? Task.CompletedTask,
            _httpsAcceptTask ?? Task.CompletedTask);

        await Task.WhenAny(shutdownTask, Task.Delay(timeout.Value));
    }

    private static Socket CreateListener(int port)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(512);
        return listener;
    }

    private async Task AcceptConnectionsAsync(Socket listener, bool isSecure, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for connection slot
                await _connectionLimit.WaitAsync(cancellationToken);

                // Accept connection
                var socket = await listener.AcceptAsync(cancellationToken);
                Console.WriteLine($"Accepted connection from {socket.RemoteEndPoint}");

                // Handle connection in background
                _ = Task.Run(() => HandleConnectionAsync(socket, isSecure, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Accept loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Accept error: {ex.Message}");
                // Log error and continue
            }
        }
        Console.WriteLine($"Accept loop exited - secure: {isSecure}");
    }

    private async Task HandleConnectionAsync(Socket socket, bool isSecure, CancellationToken cancellationToken)
    {
        var connection = _connectionPool.Get();
        _metrics.IncrementConnections();

        // SECURITY: Create timeout token to prevent Slowloris attacks
        using var requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await connection.InitializeAsync(
                socket,
                isSecure,
                _options.TlsOptions.Certificate,
                cancellationToken);

            // Check if HTTP/2 was negotiated
            if (connection.NegotiatedProtocol == "h2")
            {
                Console.WriteLine("HTTP/2 connection detected via ALPN");
                await HandleHttp2ConnectionAsync(connection, cancellationToken);
                return;
            }

            // Handle HTTP/1.1 requests (keep-alive)
            while (!cancellationToken.IsCancellationRequested)
            {
                // SECURITY: Reset timeout for each request
                requestTimeoutCts.CancelAfter(_options.RequestTimeout);
                
                // Read request with timeout
                var request = await connection.ReadRequestAsync(
                    _options.HeaderTimeout,
                    _options.MaxRequestBodySize,
                    requestTimeoutCts.Token);

                if (request == null)
                    break; // Connection closed or timeout

                _metrics.IncrementRequests();

                // Create response
                var response = new HttpResponse
                {
                    KeepAlive = request.KeepAlive
                };

                try
                {
                    // Route and handle request
                    await HandleRequestAsync(request, response, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Return error response
                    await HandleErrorAsync(ex, request, response);
                }

                // Write response
                await connection.WriteResponseAsync(response, cancellationToken);

                // Check if we should keep connection alive
                if (!response.KeepAlive || !request.KeepAlive)
                    break;

                // Check idle timeout
                var idleDuration = DateTime.UtcNow - connection.LastActivity;
                if (idleDuration > _options.IdleTimeout)
                    break;
            }
        }
        catch (Exception)
        {
            // Connection error - just close
        }
        finally
        {
            _connectionPool.Return(connection);
            _metrics.DecrementConnections();
            _connectionLimit.Release();
        }
    }

    private async Task HandleRequestAsync(HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        // Find route
        var route = _router.FindRoute(request.Method.AsSpan(), request.Path.AsSpan());
        if (route == null)
        {
            response.StatusCode = 404;
            var problemDetails = ProblemDetails.ForStatusCode(404, "The requested resource was not found", request.Path);
            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
            response.ContentType = "application/problem+json";
            return;
        }

        var routeValue = route.Value;

        // Store endpoint type in request items for middleware
        if (routeValue.EndpointType != null)
        {
            request.Items ??= new Dictionary<string, object>();
            request.Items["EndpointType"] = routeValue.EndpointType;
        }

        try
        {
            // If middleware pipeline exists, execute it with the endpoint handler as the final delegate
            if (_middlewarePipeline != null)
            {
                var middlewareResponse = await _middlewarePipeline.ExecuteAsync(
                    request,
                    (req, ct) => ExecuteEndpointAsync(req, routeValue, ct),
                    cancellationToken);

                // Copy middleware response to the response parameter
                response.StatusCode = middlewareResponse.StatusCode;
                response.Body = middlewareResponse.Body;
                response.ContentType = middlewareResponse.ContentType;
                foreach (var header in middlewareResponse.Headers)
                {
                    response.Headers[header.Key] = header.Value;
                }
                return;
            }

            Console.WriteLine($"[Server] No middleware - executing endpoint directly");
            // No middleware - execute endpoint directly
            var directResponse = await ExecuteEndpointAsync(request, routeValue, cancellationToken);
            response.StatusCode = directResponse.StatusCode;
            response.Body = directResponse.Body;
            response.ContentType = directResponse.ContentType;
            foreach (var header in directResponse.Headers)
            {
                response.Headers[header.Key] = header.Value;
            }
        }
        catch (Exception ex)
        {
            // Log exception details to console
            Console.WriteLine($"❌ EXCEPTION: {ex.GetType().Name}");
            Console.WriteLine($"   Message: {ex.Message}");
            Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"   InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            
            response.StatusCode = 500;
            var problemDetails = ProblemDetails.FromException(ex);
            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
            response.ContentType = "application/problem+json";
        }
    }

    private async ValueTask<HttpResponse> ExecuteEndpointAsync(HttpRequest request, RouteMatch route, CancellationToken cancellationToken)
    {
        var response = new HttpResponse();

        try
        {
            // Create a scope for this request if DI is enabled
            DependencyInjection.IServiceScope? scope = null;
            IServiceProvider scopedProvider = _serviceProvider ?? new NoOpServiceProvider();
            
            if (_serviceProvider is DependencyInjection.ServiceProvider sp)
            {
                scope = sp.CreateScope();
                scopedProvider = scope.ServiceProvider;
            }

            try
            {
                object? endpoint = null;
                Delegate? handler = null;
                
                // Check if we have an endpoint type (new DI-based routing) or handler (legacy)
                if (route.EndpointType != null)
                {
                    // Resolve endpoint from scoped DI container
                    endpoint = scopedProvider.GetService(route.EndpointType);
                    if (endpoint == null)
                    {
                        response.StatusCode = 500;
                        var problemDetails = ProblemDetails.ForStatusCode(500, $"Failed to resolve endpoint {route.EndpointType.Name}");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

                    // Get the HandleAsync method
                    var handleMethod = route.EndpointType.GetMethod("HandleAsync");
                    if (handleMethod == null)
                    {
                        response.StatusCode = 500;
                        var problemDetails = ProblemDetails.ForStatusCode(500, "Endpoint does not have HandleAsync method");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

                    // Get endpoint interface to find request/response types
                    var endpointInterface = route.EndpointType.GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType &&
                            (i.GetGenericTypeDefinition() == typeof(IEndpoint<,>) ||
                             i.GetGenericTypeDefinition() == typeof(IAsyncEndpoint<,>)));

                    if (endpointInterface == null)
                    {
                        response.StatusCode = 500;
                        var problemDetails = ProblemDetails.ForStatusCode(500, "Endpoint does not implement IEndpoint<,> or IAsyncEndpoint<,>");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

                    var reqType = endpointInterface.GetGenericArguments()[0];
                    var delegateType = typeof(Func<,,>).MakeGenericType(reqType, typeof(CancellationToken), handleMethod.ReturnType);
                    handler = Delegate.CreateDelegate(delegateType, endpoint, handleMethod);
                }
                else if (route.Handler != null)
                {
                    // Legacy handler-based routing
                    handler = route.Handler;
                }
                else
                {
                    response.StatusCode = 500;
                    var problemDetails = ProblemDetails.ForStatusCode(500, "No handler or endpoint type configured for route");
                    response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                    response.ContentType = "application/problem+json";
                    return response;
                }

                var handlerType = handler.GetType();
                var genericArgs = handlerType.GetGenericArguments();
                
                if (genericArgs.Length < 3) return response;

            var requestType = genericArgs[0];
            var returnType = genericArgs[2];

            // Deserialize request body
            object? requestObj = null;
            if (request.ContentLength > 0 && request.Body.Length > 0)
            {
                try
                {
                    requestObj = JsonSerializer.Deserialize(request.Body, requestType, _options.JsonOptions);
                }
                catch (Exception ex)
                {
                    response.StatusCode = 400;
                    var problemDetails = ProblemDetails.FromException(ex);
                    problemDetails.Title = "Bad Request";
                    problemDetails.Status = 400;
                    response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                    response.ContentType = "application/problem+json";
                    return response;
                }
            }
            else
            {
                requestObj = Activator.CreateInstance(requestType);
            }

            // Populate route parameters if any
            if (route.Parameters != null && requestObj != null)
            {
                foreach (var param in route.Parameters)
                {
                    var property = requestType.GetProperty(param.Key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (property != null && property.CanWrite)
                    {
                        try
                        {
                            // Try to convert the parameter value to the property type
                            object? convertedValue = null;
                            if (property.PropertyType == typeof(string))
                            {
                                convertedValue = param.Value;
                            }
                            else if (property.PropertyType == typeof(int))
                            {
                                convertedValue = int.Parse(param.Value);
                            }
                            else if (property.PropertyType == typeof(long))
                            {
                                convertedValue = long.Parse(param.Value);
                            }
                            else if (property.PropertyType == typeof(Guid))
                            {
                                convertedValue = Guid.Parse(param.Value);
                            }
                            else
                            {
                                // Try general conversion
                                convertedValue = Convert.ChangeType(param.Value, property.PropertyType);
                            }

                            property.SetValue(requestObj, convertedValue);
                        }
                        catch
                        {
                            // Ignore conversion errors - property stays at default value
                        }
                    }
                }
            }

            // Set HttpContext on endpoint instance if it has the property (for accessing request.User, etc.)
            if (endpoint != null)
            {
                var httpContextProperty = route.EndpointType?.GetProperty("HttpContext");
                if (httpContextProperty != null && httpContextProperty.CanWrite)
                {
                    httpContextProperty.SetValue(endpoint, request);
                }
            }

            // Validate request if ValidationEnabled flag is set on the request
            if (request.Items != null && request.Items.TryGetValue("ValidationEnabled", out var validationEnabledObj) && validationEnabledObj is true)
            {
                if (requestObj != null)
                {
                    // Use reflection to call .Validate() extension method from Routya.ResultKit
                    var validateMethod = typeof(Routya.ResultKit.ValidationExtensions).GetMethod("Validate");
                    if (validateMethod != null)
                    {
                        var genericMethod = validateMethod.MakeGenericMethod(requestType);
                        var validationResult = genericMethod.Invoke(null, new[] { requestObj });
                        
                        // Check if validation failed
                        var successProperty = validationResult?.GetType().GetProperty("Success");
                        var success = (bool)(successProperty?.GetValue(validationResult) ?? true);
                        
                        if (!success)
                        {
                            // Get the Error property (ProblemDetails)
                            var errorProperty = validationResult?.GetType().GetProperty("Error");
                            var problemDetails = errorProperty?.GetValue(validationResult) as Routya.ResultKit.ProblemDetails;
                            
                            if (problemDetails != null)
                            {
                                // Return validation error response
                                response.StatusCode = problemDetails.Status ?? 400;
                                response.ContentType = "application/problem+json";
                                response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, new JsonSerializerOptions
                                {
                                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                                });
                                return response;
                            }
                        }
                    }
                }
            }

            // Invoke handler
            var result = handler.DynamicInvoke(requestObj, cancellationToken);

            // Handle ValueTask<T> or Task<T>
            object? responseObj = null;
            if (result != null)
            {
                var resultType = result.GetType();
                
                // Check if it's a Task (includes Task<T> and derived types)
                if (result is Task task)
                {
                    await task;
                    
                    // Get the Result property for Task<T>
                    var resultProperty = resultType.GetProperty("Result");
                    if (resultProperty != null)
                    {
                        responseObj = resultProperty.GetValue(task);
                    }
                }
                else if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    // Convert ValueTask<T> to Task<T> and await
                    var asTaskMethod = resultType.GetMethod("AsTask");
                    var vTask = asTaskMethod?.Invoke(result, null) as Task;
                    if (vTask != null)
                    {
                        await vTask;
                        var resultProperty = vTask.GetType().GetProperty("Result");
                        responseObj = resultProperty?.GetValue(vTask);
                    }
                }
            }

            // Serialize response
            if (responseObj != null)
            {
                response.StatusCode = 200;
                response.Body = JsonSerializer.SerializeToUtf8Bytes(responseObj, _options.JsonOptions);
                response.ContentType = "application/json";
            }
            }
            finally
            {
                // Dispose scope if created
                scope?.Dispose();
            }
        }
        catch (Exception ex)
        {
            // Log exception details to console
            Console.WriteLine($"❌ EXCEPTION: {ex.GetType().Name}");
            Console.WriteLine($"   Message: {ex.Message}");
            Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"   InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            
            response.StatusCode = 500;
            var problemDetails = ProblemDetails.FromException(ex);
            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
            response.ContentType = "application/problem+json";
        }

        return response;
    }

    private async Task HandleErrorAsync(Exception exception, HttpRequest request, HttpResponse response)
    {
        response.StatusCode = 500;
        var problemDetails = ProblemDetails.FromException(exception, 500, request.Path);
        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
        response.ContentType = "application/problem+json";

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle HTTP/2 connection
    /// </summary>
    private async Task HandleHttp2ConnectionAsync(HttpConnection connection, CancellationToken cancellationToken)
    {
        // Create request handler that routes through the framework
        async Task<HttpResponse> RequestHandler(HttpRequest request)
        {
            var response = new HttpResponse();
            
            try
            {
                await HandleRequestAsync(request, response, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync(ex, request, response);
            }
            
            return response;
        }
        
        var http2Connection = new Http2Connection(connection.Stream!, RequestHandler);
        
        try
        {
            await http2Connection.ProcessAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP/2 connection error: {ex.Message}");
        }
        finally
        {
            await http2Connection.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _httpListener?.Dispose();
        _httpsListener?.Dispose();
        _connectionLimit.Dispose();
        _shutdownCts.Dispose();
    }
}

/// <summary>
/// No-op service provider for when DI is not configured
/// </summary>
internal sealed class NoOpServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
