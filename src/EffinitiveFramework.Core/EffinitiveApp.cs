using System.Reflection;
using System.Text.Json;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.DependencyInjection;
using EffinitiveFramework.Core.Middleware;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.StaticFiles;
using EffinitiveFramework.Core.WebSocket;

namespace EffinitiveFramework.Core;

/// <summary>
/// Builder for EffinitiveApp with fluent configuration
/// </summary>
public sealed class EffinitiveAppBuilder
{
    private readonly ServerOptions _serverOptions = new();
    private readonly Router _router = new();
    private readonly ServiceCollection _services = new();
    private readonly List<Action<MiddlewarePipeline>> _middlewareConfigurators = new();
    private Assembly? _endpointsAssembly;
    private StaticFileHandler? _staticFileHandler;

    /// <summary>
    /// Configure services for dependency injection
    /// </summary>
    public EffinitiveAppBuilder ConfigureServices(Action<ServiceCollection> configure)
    {
        configure(_services);
        return this;
    }

    /// <summary>
    /// Add middleware to the pipeline
    /// </summary>
    public EffinitiveAppBuilder UseMiddleware<TMiddleware>() where TMiddleware : IMiddleware
    {
        _middlewareConfigurators.Add(pipeline => pipeline.Use<TMiddleware>());
        return this;
    }

    /// <summary>
    /// Add middleware using inline lambda
    /// </summary>
    public EffinitiveAppBuilder Use(Func<HttpRequest, RequestDelegate, CancellationToken, ValueTask<HttpResponse>> middleware)
    {
        _middlewareConfigurators.Add(pipeline => pipeline.Use(middleware));
        return this;
    }

    /// <summary>
    /// Enable response compression middleware (gzip).
    /// Compresses responses for clients that support gzip based on Accept-Encoding header.
    /// </summary>
    public EffinitiveAppBuilder UseResponseCompression(
        System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Fastest,
        int minimumSize = 1024)
    {
        _middlewareConfigurators.Add(pipeline => 
            pipeline.Use(new ResponseCompressionMiddleware(compressionLevel, minimumSize)));
        return this;
    }

    /// <summary>
    /// Enable automatic request validation using Routya.ResultKit.
    /// Validates request bodies using System.ComponentModel.DataAnnotations and custom attributes.
    /// </summary>
    public EffinitiveAppBuilder UseValidation()
    {
        // Add middleware that sets validation flag on requests
        _middlewareConfigurators.Add(pipeline => pipeline.Use(async (request, next, ct) =>
        {
            request.Items ??= new Dictionary<string, object>();
            request.Items["ValidationEnabled"] = true;
            return await next(request, ct);
        }));
        return this;
    }

    /// <summary>
    /// Configure JSON serialization options
    /// </summary>
    public EffinitiveAppBuilder ConfigureJson(Action<JsonSerializerOptions> configure)
    {
        configure(_serverOptions.JsonOptions);
        return this;
    }

    /// <summary>
    /// Configure TLS/HTTPS settings
    /// </summary>
    public EffinitiveAppBuilder ConfigureTls(Action<TlsOptions> configure)
    {
        configure(_serverOptions.TlsOptions);
        return this;
    }

    /// <summary>
    /// Set HTTP port (default: 5000, set to 0 to disable HTTP)
    /// </summary>
    public EffinitiveAppBuilder UsePort(int port)
    {
        _serverOptions.HttpPort = port;
        return this;
    }

    /// <summary>
    /// Set HTTPS port (default: 0/disabled, requires TLS configuration)
    /// </summary>
    public EffinitiveAppBuilder UseHttpsPort(int port)
    {
        _serverOptions.HttpsPort = port;
        return this;
    }

    /// <summary>
    /// Set maximum concurrent connections
    /// </summary>
    public EffinitiveAppBuilder UseMaxConnections(int maxConnections)
    {
        _serverOptions.MaxConcurrentConnections = maxConnections;
        return this;
    }

    /// <summary>
    /// Configure server options directly
    /// </summary>
    public EffinitiveAppBuilder Configure(Action<ServerOptions> configure)
    {
        configure(_serverOptions);
        return this;
    }

    /// <summary>
    /// Set idle connection timeout
    /// </summary>
    public EffinitiveAppBuilder UseIdleTimeout(TimeSpan timeout)
    {
        _serverOptions.IdleTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Map a WebSocket endpoint at the given path.
    /// The handler receives a WebSocketConnection for bidirectional message exchange.
    /// </summary>
    public EffinitiveAppBuilder MapWebSocket(string path, Func<WebSocketConnection, CancellationToken, Task> handler)
    {
        _router.AddWebSocketRoute(path, handler);
        return this;
    }

    /// <summary>
    /// Enable static file serving from the specified root directory.
    /// Files are pre-loaded into memory at startup for zero per-request I/O.
    /// </summary>
    public EffinitiveAppBuilder UseStaticFiles(string rootPath, string requestPath = "/static", string? cacheControl = "public, max-age=3600")
    {
        _staticFileHandler = new StaticFileHandler(new StaticFileOptions
        {
            RootPath = rootPath,
            RequestPath = requestPath,
            CacheControl = cacheControl
        });
        return this;
    }

    /// <summary>
    /// Enable static file serving with custom options.
    /// </summary>
    public EffinitiveAppBuilder UseStaticFiles(Action<StaticFileOptions> configure)
    {
        var options = new StaticFileOptions();
        configure(options);
        _staticFileHandler = new StaticFileHandler(options);
        return this;
    }

    /// <summary>
    /// Map endpoints from specified assembly
    /// </summary>
    public EffinitiveAppBuilder MapEndpoints(Assembly assembly)
    {
        _endpointsAssembly = assembly;
        return this;
    }

    /// <summary>
    /// Map endpoints from calling assembly
    /// </summary>
    public EffinitiveAppBuilder MapEndpoints()
    {
        _endpointsAssembly = Assembly.GetCallingAssembly();
        return this;
    }

    /// <summary>
    /// Build the EffinitiveApp
    /// </summary>
    public EffinitiveApp Build()
    {
        // Auto-register endpoints from assembly if specified
        if (_endpointsAssembly != null)
        {
            foreach (var type in _endpointsAssembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Where(t => typeof(IEndpoint).IsAssignableFrom(t)))
            {
                _services.AddTransient(type, type);
            }
        }

        // Build service provider
        var serviceProvider = _services.BuildServiceProvider();
        
        // Create middleware pipeline only when middleware is configured.
        MiddlewarePipeline? middlewarePipeline = null;
        if (_middlewareConfigurators.Count > 0)
        {
            middlewarePipeline = new MiddlewarePipeline(serviceProvider);

            // Configure middleware
            foreach (var configurator in _middlewareConfigurators)
            {
                configurator(middlewarePipeline);
            }
        }
        
        // Register endpoints if assembly specified
        if (_endpointsAssembly != null)
        {
            RegisterEndpoints(_endpointsAssembly, serviceProvider);
        }

        // Freeze router: materialises FrozenDictionary and pre-splits parameterised routes.
        // Must be called after all AddRoute / AddEndpointType calls.
        _router.Freeze();

        return new EffinitiveApp(_serverOptions, _router, serviceProvider, middlewarePipeline, _staticFileHandler);
    }

    private void RegisterEndpoints(Assembly assembly, IServiceProvider serviceProvider)
    {
        // ── Generic endpoints: IEndpoint<TReq, TRes> and IAsyncEndpoint<TReq, TRes> ─────────────
        var endpointTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IEndpoint<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IAsyncEndpoint<,>))));

        foreach (var type in endpointTypes)
        {
            var methodProp = type.GetProperty("Method", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var routeProp = type.GetProperty("Route", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            string? method = null;
            string? route = null;

            object? tempInstance = null;
            try { tempInstance = serviceProvider.GetService(type); } catch { }

            if (tempInstance == null)
            {
                try { tempInstance = Activator.CreateInstance(type); }
                catch
                {
                    Console.WriteLine($"Warning: Could not register endpoint {type.Name} - unable to create instance for metadata extraction");
                    continue;
                }
            }

            if (tempInstance != null)
            {
                method = methodProp?.GetValue(tempInstance)?.ToString() ?? "GET";
                route = routeProp?.GetValue(tempInstance)?.ToString() ?? "/";
            }
            else
            {
                continue;
            }

            var invoker = EndpointInvoker.Build(type);
            _router.AddEndpointType(method, route, type, invoker);
            Console.WriteLine($"✅ Registered: {method.ToUpper(),-6} {route,-25} -> {type.Name}");
        }

        // ── Non-generic IEndpoint implementations (SSE, custom execute-pattern endpoints) ───────
        // These expose GetMethod()/GetRoute() and are invoked via ExecuteAsync(HttpRequest, ct).
        var alreadyRegistered = endpointTypes.ToHashSet();
        var specialEndpointTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => !alreadyRegistered.Contains(t))
            .Where(t => typeof(IEndpoint).IsAssignableFrom(t))
            .Where(t => t.GetMethod("GetMethod") != null && t.GetMethod("GetRoute") != null);

        foreach (var type in specialEndpointTypes)
        {
            object? tempInstance = null;
            try { tempInstance = serviceProvider.GetService(type); } catch { }
            if (tempInstance == null)
            {
                try { tempInstance = Activator.CreateInstance(type); }
                catch
                {
                    Console.WriteLine($"Warning: Could not register special endpoint {type.Name}");
                    continue;
                }
            }

            var getMethod = type.GetMethod("GetMethod");
            var getRoute  = type.GetMethod("GetRoute");
            var method = getMethod?.Invoke(tempInstance, null)?.ToString() ?? "GET";
            var route  = getRoute?.Invoke(tempInstance, null)?.ToString() ?? "/";

            // No compiled invoker — will fall through to ExecuteAsync slow path
            _router.AddEndpointType(method, route, type, null);
            Console.WriteLine($"✅ Registered: {method.ToUpper(),-6} {route,-25} -> {type.Name}");
        }
    }
}

/// <summary>
/// Main application class for Effinitive Framework
/// </summary>
public sealed class EffinitiveApp : IDisposable
{
    private readonly EffinitiveServer _server;
    private readonly ServerOptions _options;
    private readonly IServiceProvider? _serviceProvider;
    private readonly MiddlewarePipeline? _middlewarePipeline;

    /// <summary>
    /// Server metrics
    /// </summary>
    public ServerMetrics Metrics => _server.Metrics;

    /// <summary>
    /// Service provider for dependency injection (null if DI not configured)
    /// </summary>
    public IServiceProvider? Services => _serviceProvider;

    internal EffinitiveApp(ServerOptions options, Router router, IServiceProvider? serviceProvider = null, MiddlewarePipeline? middlewarePipeline = null, StaticFileHandler? staticFileHandler = null)
    {
        _options = options;
        _serviceProvider = serviceProvider;
        _middlewarePipeline = middlewarePipeline;
        _server = new EffinitiveServer(options, router, serviceProvider, middlewarePipeline, staticFileHandler);
    }

    /// <summary>
    /// Create a new EffinitiveApp builder
    /// </summary>
    public static EffinitiveAppBuilder Create()
    {
        return new EffinitiveAppBuilder();
    }

    /// <summary>
    /// Start the server
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _server.StartAsync(cancellationToken);

            Console.WriteLine($"EffinitiveFramework listening on:");
            if (_options.HttpPort > 0)
                Console.WriteLine($"  http://localhost:{_options.HttpPort}");
            if (_options.HttpsPort > 0)
                Console.WriteLine($"  https://localhost:{_options.HttpsPort}");

            // Wait for cancellation
            if (cancellationToken == default)
            {
                // If no cancellation token provided, create one that never cancels
                await Task.Delay(Timeout.Infinite);
            }
            else
            {
                // Wait for cancellation
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Graceful shutdown
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Stop the server gracefully
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        await _server.StopAsync(timeout);
    }

    public void Dispose()
    {
        _server.Dispose();
    }
}

/// <summary>
/// Empty request marker for endpoints that don't need a request body
/// </summary>
public readonly struct EmptyRequest
{
}
