using System.Text.Json;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core;

public sealed partial class EffinitiveServer
{
    private async Task HandleRequestAsync(HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        // Handle asterisk-form target: only OPTIONS allowed with *
        if (request.Path == "*")
        {
            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 204;
                response.Headers["Allow"] = "GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS";
                response.Body = Array.Empty<byte>();
                return;
            }
            // Any other method with * target is invalid
            response.StatusCode = 400;
            response.Body = System.Text.Encoding.UTF8.GetBytes("Asterisk-form request-target only valid for OPTIONS");
            response.ContentType = "text/plain";
            response.KeepAlive = false;
            return;
        }

        // Handle OPTIONS for specific paths
        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 204;
            response.Headers["Allow"] = "GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS";
            response.Body = Array.Empty<byte>();
            return;
        }

        // Reject CONNECT method (not supported by origin servers)
        if (request.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 405;
            response.Body = System.Text.Encoding.UTF8.GetBytes("CONNECT method not supported");
            response.ContentType = "text/plain";
            response.KeepAlive = false;
            return;
        }

        // Handle Upgrade requests: respond with 426 Upgrade Required
        if (request.Headers.ContainsKey("Upgrade") &&
            request.Headers.TryGetValue("Connection", out var connVal) &&
            connVal.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 426;
            response.Body = System.Text.Encoding.UTF8.GetBytes("Upgrade Required");
            response.ContentType = "text/plain";
            return;
        }

        // For HEAD, find the GET route
        var methodForRoute = request.Method;
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            methodForRoute = "GET";
        }

        // Find route
        var route = _router.FindRoute(methodForRoute.AsSpan(), request.Path.AsSpan());

        if (route == null)
        {
            // Check if the method is known
            bool isKnownMethod = request.Method is "GET" or "POST" or "PUT" or "DELETE" or
                "PATCH" or "HEAD" or "OPTIONS" or "TRACE" or "CONNECT";
            if (!isKnownMethod)
            {
                response.StatusCode = 501;
                response.Body = System.Text.Encoding.UTF8.GetBytes($"Method {request.Method} not implemented");
                response.ContentType = "text/plain";
                response.KeepAlive = false;
                return;
            }

            // Check if the path exists for other methods (405 Method Not Allowed)
            var allowedMethods = _router.GetAllowedMethods(request.Path.AsSpan());
            if (allowedMethods != null && allowedMethods.Count > 0)
            {
                // Add HEAD and OPTIONS which are always implicitly allowed
                if (!allowedMethods.Contains("HEAD") && allowedMethods.Contains("GET"))
                    allowedMethods.Add("HEAD");
                allowedMethods.Add("OPTIONS");

                response.StatusCode = 405;
                response.Headers["Allow"] = string.Join(", ", allowedMethods);
                response.Body = System.Text.Encoding.UTF8.GetBytes("Method Not Allowed");
                response.ContentType = "text/plain";
                return;
            }

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
                    if (request.ContentLength > 0 && request.Body.Length > 0 &&
                        invoker.RequestType != typeof(EmptyRequest))
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
                return await ExecuteEndpointSlowPathAsync(request, route, scopedProvider, cancellationToken);
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

    private async ValueTask<HttpResponse> ExecuteEndpointSlowPathAsync(
        HttpRequest request, RouteMatch route, IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var response = new HttpResponse();

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
        if (request.ContentLength > 0 && request.Body.Length > 0 &&
            requestType != typeof(EmptyRequest))
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

        return response;
    }
}
