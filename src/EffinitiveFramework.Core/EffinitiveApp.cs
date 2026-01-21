using System.Reflection;
using System.Text.Json;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.DependencyInjection;
using EffinitiveFramework.Core.Middleware;
using EffinitiveFramework.Core.Http;

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
            var endpointTypes = _endpointsAssembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Where(t => t.GetInterfaces().Any(i =>
                    i.IsGenericType &&
                    (i.GetGenericTypeDefinition() == typeof(IEndpoint<,>) ||
                     i.GetGenericTypeDefinition() == typeof(IAsyncEndpoint<,>))));

            foreach (var type in endpointTypes)
            {
                // Register as transient (new instance per request)
                _services.AddTransient(type, type);
            }
        }

        // Build service provider
        var serviceProvider = _services.BuildServiceProvider();
        
        // Create middleware pipeline with DI support
        var middlewarePipeline = new MiddlewarePipeline(serviceProvider);
        
        // Configure middleware
        foreach (var configurator in _middlewareConfigurators)
        {
            configurator(middlewarePipeline);
        }
        
        // Register endpoints if assembly specified
        if (_endpointsAssembly != null)
        {
            RegisterEndpoints(_endpointsAssembly, serviceProvider);
        }

        return new EffinitiveApp(_serverOptions, _router, serviceProvider, middlewarePipeline);
    }

    private void RegisterEndpoints(Assembly assembly, IServiceProvider serviceProvider)
    {
        var endpointTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IEndpoint<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IAsyncEndpoint<,>))));

        foreach (var type in endpointTypes)
        {
            // Get route and method from properties without creating instance
            var methodProp = type.GetProperty("Method", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var routeProp = type.GetProperty("Route", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            // These are protected abstract properties, so we need to check the base class for defaults
            // or try to instantiate if there's a parameterless constructor
            string? method = null;
            string? route = null;

            // Try to get from attributes or conventions first
            // For now, we'll need to create a temporary instance with DI if possible
            object? tempInstance = null;
            try
            {
                // Try to resolve from DI (will work if dependencies are registered)
                tempInstance = serviceProvider.GetService(type);
            }
            catch
            {
                // Ignore - will try Activator next
            }

            if (tempInstance == null)
            {
                try
                {
                    // Try parameterless constructor
                    tempInstance = Activator.CreateInstance(type);
                }
                catch
                {
                    // Skip this endpoint - can't get metadata without an instance
                    // In production, you'd use attributes like [HttpGet("/api/route")]
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

            // Register endpoint type as metadata - will be resolved per-request with DI
            _router.AddEndpointType(method, route, type);
            Console.WriteLine($"✅ Registered: {method.ToUpper().PadRight(6)} {route.PadRight(25)} -> {type.Name}");
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

    internal EffinitiveApp(ServerOptions options, Router router, IServiceProvider? serviceProvider = null, MiddlewarePipeline? middlewarePipeline = null)
    {
        _options = options;
        _serviceProvider = serviceProvider;
        _middlewarePipeline = middlewarePipeline;
        _server = new EffinitiveServer(options, router, serviceProvider, middlewarePipeline);
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
