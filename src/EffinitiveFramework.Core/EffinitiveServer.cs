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
    private readonly bool _isProduction;
    private int _activeConnections;
    
    private Socket? _httpListener;
    private Socket? _httpsListener;
    private Task? _httpAcceptTask;
    private Task? _httpsAcceptTask;

    // Reusable argument arrays for MethodInfo.Invoke in the slow path.
    // [ThreadStatic] is safe here: Invoke is synchronous and does not retain the array
    // across the subsequent await, so each thread's slot is always free before the next call.
    [ThreadStatic] private static object[]? _slowPathArgs1;
    [ThreadStatic] private static object[]? _slowPathArgs2;
    [ThreadStatic] private static object[]? _slowPathValidArgs;

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
        _isProduction = !options.EnableDebugLogging;
        
        // Configure ThreadPool for high-concurrency scenarios
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIOThreads);
        var optimalThreads = Math.Max(Environment.ProcessorCount * 2, minWorkerThreads);
        ThreadPool.SetMinThreads(optimalThreads, minIOThreads);
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
        
        // Performance optimizations for high-concurrency
        listener.NoDelay = true; // Disable Nagle's algorithm
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, false);
        
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(8192); // Increased backlog for stress tests
        return listener;
    }

    private async Task AcceptConnectionsAsync(Socket listener, bool isSecure, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check connection limit using atomic counter instead of semaphore wait
                var currentConnections = Interlocked.CompareExchange(ref _activeConnections, 0, 0);
                if (currentConnections >= _options.MaxConcurrentConnections)
                {
                    await Task.Delay(10, cancellationToken); // Brief backoff
                    continue;
                }

                // Accept connection
                var socket = await listener.AcceptAsync(cancellationToken);
                
                // Apply socket optimizations
                socket.NoDelay = true;
                socket.SendBufferSize = 8192;
                socket.ReceiveBufferSize = 8192;
                
                if (!_isProduction)
                    Console.WriteLine($"Accepted connection from {socket.RemoteEndPoint}");

                // Increment counter and handle directly (no Task.Run overhead)
                Interlocked.Increment(ref _activeConnections);
                _ = HandleConnectionAsync(socket, isSecure, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!_isProduction)
                    Console.WriteLine("Accept loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                if (!_isProduction)
                    Console.WriteLine($"Accept error: {ex.Message}");
                // Log error and continue
            }
        }
        if (!_isProduction)
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
                if (!_isProduction)
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
            Interlocked.Decrement(ref _activeConnections);
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

            if (!_isProduction)
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
                // ---------------------------------------------------------------
                // Fast path: compiled EndpointInvoker (zero reflection on hot path)
                // ---------------------------------------------------------------
                if (route.Invoker != null && route.EndpointType != null)
                {
                    var invoker = route.Invoker;

                    // Resolve endpoint from scoped DI container
                    var endpoint = scopedProvider.GetService(route.EndpointType);
                    if (endpoint == null)
                    {
                        response.StatusCode = 500;
                        var pd = ProblemDetails.ForStatusCode(500, $"Failed to resolve endpoint {route.EndpointType.Name}");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(pd, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

                    // Store route parameters for middleware/endpoint access
                    if (route.Parameters != null && route.Parameters.Count > 0)
                    {
                        request.RouteValues = route.Parameters;
                        request.Items ??= new Dictionary<string, object>();
                        request.Items["RouteParameters"] = route.Parameters;
                    }

                    // Set HttpContext — compiled delegate, no reflection
                    invoker.SetHttpContext(endpoint, request);

                    // Deserialize request body
                    object? requestObj = null;
                    if (request.ContentLength > 0 && request.Body.Length > 0)
                    {
                        try
                        {
                            requestObj = JsonSerializer.Deserialize(request.Body, invoker.RequestType, _options.JsonOptions);
                        }
                        catch (Exception ex)
                        {
                            response.StatusCode = 400;
                            var pd = ProblemDetails.FromException(ex);
                            pd.Title = "Bad Request";
                            pd.Status = 400;
                            response.Body = JsonSerializer.SerializeToUtf8Bytes(pd, _options.JsonOptions);
                            response.ContentType = "application/problem+json";
                            return response;
                        }
                    }
                    else
                    {
                        requestObj = Activator.CreateInstance(invoker.RequestType);
                    }

                    // Bind route parameters — compiled setters, no PropertyInfo.SetValue/boxing
                    if (route.Parameters != null && requestObj != null)
                    {
                        foreach (var param in route.Parameters)
                        {
                            if (invoker.RouteParamSetters.TryGetValue(param.Key, out var setter))
                            {
                                try
                                {
                                    setter.Setter(requestObj, ConvertRouteParam(param.Value, setter.Property.PropertyType));
                                }
                                catch
                                {
                                    // Ignore conversion errors — property stays at default value
                                }
                            }
                        }
                    }

                    // Invoke handler — compiled delegate, no MethodInfo.Invoke/boxing
                    var responseObj = await invoker.InvokeAsync(endpoint, requestObj, cancellationToken);

                    // ContentType — compiled getter, no GetProperty/GetValue
                    var contentType = invoker.GetContentType(endpoint) ?? "application/json";

                    SerializeResponse(response, responseObj, contentType);
                    return response;
                }

                // ---------------------------------------------------------------
                // Slow path: endpoint type without compiled invoker, or legacy handler
                // ---------------------------------------------------------------
                {
                    object? endpoint = null;
                    Delegate? handler = null;
                    Type? requestType = null;
                    System.Reflection.MethodInfo? handleMethod = null;

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

                        handleMethod = route.EndpointType.GetMethod("HandleAsync",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (handleMethod == null)
                        {
                            response.StatusCode = 500;
                            var problemDetails = ProblemDetails.ForStatusCode(500, "Endpoint does not have public HandleAsync method");
                            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                            response.ContentType = "application/problem+json";
                            return response;
                        }

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

                        requestType = endpointInterface.GetGenericArguments()[0];
                    }
                    else if (route.Handler != null)
                    {
                        handler = route.Handler;
                        var handlerType = handler.GetType();
                        var genericArgs = handlerType.GetGenericArguments();

                        if (genericArgs.Length < 3)
                        {
                            response.StatusCode = 500;
                            var problemDetails = ProblemDetails.ForStatusCode(500, "Handler does not have correct generic arguments");
                            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                            response.ContentType = "application/problem+json";
                            return response;
                        }

                        requestType = genericArgs[0];
                    }
                    else
                    {
                        response.StatusCode = 500;
                        var problemDetails = ProblemDetails.ForStatusCode(500, "No handler or endpoint type configured for route");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

                    if (requestType == null)
                    {
                        response.StatusCode = 500;
                        var problemDetails = ProblemDetails.ForStatusCode(500, "Could not determine request type");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

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
                                    property.SetValue(requestObj, ConvertRouteParam(param.Value, property.PropertyType));
                                }
                                catch
                                {
                                    // Ignore conversion errors - property stays at default value
                                }
                            }
                        }
                    }

                    // Set HttpContext on endpoint instance if it has the property
                    if (endpoint != null)
                    {
                        if (route.Parameters != null && route.Parameters.Count > 0)
                        {
                            request.RouteValues = route.Parameters;
                            request.Items ??= new Dictionary<string, object>();
                            request.Items["RouteParameters"] = route.Parameters;
                        }

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
                            var validateMethod = typeof(Routya.ResultKit.ValidationExtensions).GetMethod("Validate");
                            if (validateMethod != null)
                            {
                                var genericMethod = validateMethod.MakeGenericMethod(requestType);
                                var vArgs = _slowPathValidArgs ??= new object[1];
                                vArgs[0] = requestObj;
                                var validationResult = genericMethod.Invoke(null, vArgs);

                                var successProperty = validationResult?.GetType().GetProperty("Success");
                                var success = (bool)(successProperty?.GetValue(validationResult) ?? true);

                                if (!success)
                                {
                                    var errorProperty = validationResult?.GetType().GetProperty("Error");
                                    var problemDetails = errorProperty?.GetValue(validationResult) as Routya.ResultKit.ProblemDetails;

                                    if (problemDetails != null)
                                    {
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
                    object? result = null;

                    if (handleMethod != null && endpoint != null)
                    {
                        var parameters = handleMethod.GetParameters();

                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken))
                        {
                            var a1 = _slowPathArgs1 ??= new object[1];
                            a1[0] = cancellationToken;
                            result = handleMethod.Invoke(endpoint, a1);
                        }
                        else if (parameters.Length == 2)
                        {
                            var a2 = _slowPathArgs2 ??= new object[2];
                            a2[0] = requestObj;
                            a2[1] = cancellationToken;
                            result = handleMethod.Invoke(endpoint, a2);
                        }
                        else
                        {
                            response.StatusCode = 500;
                            var problemDetails = ProblemDetails.ForStatusCode(500, $"Unexpected parameter count: {parameters.Length}");
                            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                            response.ContentType = "application/problem+json";
                            return response;
                        }
                    }
                    else if (handler != null)
                    {
                        result = handler.DynamicInvoke(requestObj, cancellationToken);
                    }
                    else
                    {
                        response.StatusCode = 500;
                        var problemDetails = ProblemDetails.ForStatusCode(500, "No handler or method to invoke");
                        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
                        response.ContentType = "application/problem+json";
                        return response;
                    }

                    // Handle ValueTask<T> or Task<T>
                    object? responseObj = null;
                    if (result != null)
                    {
                        var resultType = result.GetType();

                        if (result is Task task)
                        {
                            await task;
                            var resultProperty = resultType.GetProperty("Result");
                            if (resultProperty != null)
                                responseObj = resultProperty.GetValue(task);
                        }
                        else if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                        {
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
                    var contentType = "application/json";
                    if (endpoint != null)
                    {
                        var contentTypeProperty = route.EndpointType?.GetProperty("ContentType",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public);
                        if (contentTypeProperty != null && contentTypeProperty.CanRead)
                        {
                            var endpointContentType = contentTypeProperty.GetValue(endpoint) as string;
                            if (!string.IsNullOrEmpty(endpointContentType))
                                contentType = endpointContentType;
                        }
                    }
                    SerializeResponse(response, responseObj, contentType);
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
            // Log exception details only in debug mode
            if (!_isProduction)
            {
                Console.WriteLine($"❌ EXCEPTION: {ex.GetType().Name}");
                Console.WriteLine($"   Message: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"   InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            }
            
            response.StatusCode = 500;
            var problemDetails = ProblemDetails.FromException(ex);
            response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
            response.ContentType = "application/problem+json";
        }

        return response;
    }

    private static object? ConvertRouteParam(string value, Type targetType)
    {
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(int))   return int.Parse(value);
        if (targetType == typeof(long))  return long.Parse(value);
        if (targetType == typeof(Guid))  return Guid.Parse(value);
        return Convert.ChangeType(value, targetType);
    }

    private void SerializeResponse(HttpResponse response, object? responseObj, string contentType)
    {
        if (responseObj != null)
        {
            response.StatusCode = 200;
            response.ContentType = contentType;

            if (contentType == "text/plain")
            {
                response.Body = responseObj is string str
                    ? System.Text.Encoding.UTF8.GetBytes(str)
                    : System.Text.Encoding.UTF8.GetBytes(responseObj.ToString() ?? "");
            }
            else
            {
                response.Body = JsonSerializer.SerializeToUtf8Bytes(responseObj, _options.JsonOptions);
            }
        }
        else
        {
            response.StatusCode = 200;
            response.Body = Array.Empty<byte>();
            response.ContentType = "text/plain";
        }
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
            if (!_isProduction)
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
