using System.Reflection;
using System.Text.Json;
using EffinitiveFramework.Core.Caching;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.DependencyInjection;
using EffinitiveFramework.Core.Middleware;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.OpenApi;
using EffinitiveFramework.Core.RateLimiting;
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
    private OpenApiOptions? _openApiOptions;
    private RateLimitOptions? _rateLimitOptions;

    // Registration order of the compression and caching middleware, used to warn about the one
    // ordering that silently misbehaves (see Build()). -1 means "not registered".
    private int _compressionOrder = -1;
    private int _cachingOrder = -1;

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
        _compressionOrder = _middlewareConfigurators.Count;
        _middlewareConfigurators.Add(pipeline =>
            pipeline.Use(new ResponseCompressionMiddleware(compressionLevel, minimumSize)));
        return this;
    }

    /// <summary>
    /// Enable response caching. Repeat GET/HEAD requests to endpoints marked with
    /// <see cref="ResponseCacheAttribute"/> are served from an in-process store without running the
    /// endpoint, and the matching <c>Cache-Control</c> / <c>Vary</c> / <c>Age</c> headers are emitted
    /// for client and proxy caches.
    /// <para>
    /// Caching is opt-in: endpoints without the attribute are unaffected. Call this <i>after</i>
    /// <see cref="UseResponseCompression"/> so compression stays outermost and cache hits are still
    /// compressed.
    /// </para>
    /// </summary>
    public EffinitiveAppBuilder UseResponseCaching(Action<ResponseCacheOptions>? configure = null)
    {
        var options = new ResponseCacheOptions();
        configure?.Invoke(options);

        _cachingOrder = _middlewareConfigurators.Count;
        _middlewareConfigurators.Add(pipeline =>
            pipeline.Use(new ResponseCacheMiddleware(options)));
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
    /// Add a listener beyond <see cref="UsePort"/> and <see cref="UseHttpsPort"/>,
    /// with its own certificate and its own ALPN list.
    /// </summary>
    /// <example>
    /// An HTTP/1.1-only TLS listener, for a port that must not be upgraded to HTTP/2:
    /// <code>
    /// .AddListener(l =>
    /// {
    ///     l.Port = 8081;
    ///     l.UseTls = true;
    ///     l.Tls.CertificatePath = "/certs/server.crt";
    ///     l.Tls.KeyPath = "/certs/server.key";
    ///     l.AlpnProtocols = [SslApplicationProtocol.Http11];
    /// })
    /// </code>
    /// </example>
    public EffinitiveAppBuilder AddListener(Action<ListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var listener = new ListenerOptions();
        configure(listener);
        _serverOptions.Listeners.Add(listener);
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
    /// Files are streamed from disk per request with ETag/Last-Modified, conditional
    /// requests, range support, and Accept-Encoding negotiation of pre-compressed sidecars.
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
    /// Serve an OpenAPI 3.0 document describing the registered endpoints, plus a Swagger UI page.
    /// <para>
    /// The document is generated once at <see cref="Build"/> time from the frozen route table, so it
    /// costs nothing per request. Endpoints are described automatically; use
    /// <c>[OpenApiOperation]</c>, <c>[OpenApiResponse]</c>, and <c>[OpenApiParameter]</c> to add prose,
    /// extra status codes, and query/header parameters, and <c>[OpenApiIgnore]</c> to leave an endpoint
    /// out.
    /// </para>
    /// </summary>
    public EffinitiveAppBuilder UseOpenApi(Action<OpenApiOptions>? configure = null)
    {
        var options = new OpenApiOptions();
        configure?.Invoke(options);
        _openApiOptions = options;
        return this;
    }

    /// <summary>
    /// Enable per-client rate limiting. A token-bucket allowance is tracked per client IP: callers may
    /// burst up to <c>PermitLimit</c> requests and then sustain the refill rate, and requests over the
    /// limit get <c>429 Too Many Requests</c> with <c>Retry-After</c>.
    /// <para>
    /// The limit applies server-wide, including static files, the OpenAPI document, and unrouted paths.
    /// Use <c>[RateLimit]</c> to give an endpoint a tighter allowance of its own, and
    /// <c>[DisableRateLimit]</c> to exempt one entirely.
    /// </para>
    /// <para>
    /// Behind a reverse proxy, every request arrives from the proxy's address — call
    /// <c>AddTrustedProxy()</c> so the client is taken from <c>X-Forwarded-For</c> instead. Without
    /// that, the header is ignored, because trusting it from arbitrary callers would let anyone bypass
    /// the limit.
    /// </para>
    /// </summary>
    public EffinitiveAppBuilder UseRateLimiting(Action<RateLimitOptions>? configure = null)
    {
        var options = new RateLimitOptions();
        configure?.Invoke(options);
        _rateLimitOptions = options;
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
        
        // The pipeline runs first-registered outermost. Caching must sit inside compression so that a
        // cache hit still returns through the compression middleware — the other way round, hits
        // short-circuit past it and go out uncompressed.
        if (_cachingOrder >= 0 && _compressionOrder >= 0 && _cachingOrder < _compressionOrder)
        {
            Console.WriteLine(
                "⚠️  UseResponseCaching() was registered before UseResponseCompression() — cache hits will be served uncompressed. Swap the calls.");
        }

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

        // Generate the OpenAPI document once, after freezing — it reads the route table the freeze
        // produces, and the result is a static byte buffer for the lifetime of the app.
        OpenApiHandler? openApiHandler = null;
        if (_openApiOptions != null)
        {
            openApiHandler = new OpenApiHandler(
                _openApiOptions,
                _router.GetRegisteredRoutes(),
                _serverOptions.JsonOptions);
        }

        var rateLimiter = _rateLimitOptions != null
            ? new RateLimiter(_rateLimitOptions, _serverOptions.JsonOptions)
            : null;

        return new EffinitiveApp(_serverOptions, _router, serviceProvider, middlewarePipeline, _staticFileHandler, openApiHandler, rateLimiter);
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

    internal EffinitiveApp(ServerOptions options, Router router, IServiceProvider? serviceProvider = null, MiddlewarePipeline? middlewarePipeline = null, StaticFileHandler? staticFileHandler = null, OpenApiHandler? openApiHandler = null, RateLimiter? rateLimiter = null)
    {
        _options = options;
        _serviceProvider = serviceProvider;
        _middlewarePipeline = middlewarePipeline;
        _server = new EffinitiveServer(options, router, serviceProvider, middlewarePipeline, staticFileHandler, openApiHandler, rateLimiter);
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
