using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.ObjectPool;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;

namespace EffinitiveFramework.Core;

/// <summary>
/// High-performance HTTP server with TLS support.
/// Split across partial class files:
///   - EffinitiveServer.cs                  (this file: core infrastructure)
///   - EffinitiveServer.ConnectionHandling.cs (per-connection request loop)
///   - EffinitiveServer.RequestValidation.cs  (pre-routing security checks, conditional headers)
///   - EffinitiveServer.RequestHandling.cs    (routing, endpoint execution)
///   - EffinitiveServer.Helpers.cs            (ETag, serialization, HTTP/2, utilities)
/// </summary>
public sealed partial class EffinitiveServer : IDisposable
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
    private readonly DateTime _serverStartTime;
    private readonly string _serverStartTimeRfc;
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
        // Truncate to seconds to match HTTP date precision (RFC 7231 §7.1.1.1)
        var now = DateTime.UtcNow;
        _serverStartTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
        _serverStartTimeRfc = _serverStartTime.ToString("R");
        
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

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _httpListener?.Dispose();
        _httpsListener?.Dispose();
        _connectionLimit.Dispose();
        _shutdownCts.Dispose();
    }
}
