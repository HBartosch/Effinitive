using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.ObjectPool;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;
using EffinitiveFramework.Core.OpenApi;
using EffinitiveFramework.Core.RateLimiting;
using EffinitiveFramework.Core.StaticFiles;
using EffinitiveFramework.Core.Transport;
#if NET10_0_OR_GREATER
using System.Net.Quic;
using System.Net.Security;
using EffinitiveFramework.Core.Http3;
#endif

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
    private readonly StaticFileHandler? _staticFileHandler;
    private readonly OpenApiHandler? _openApiHandler;
    private readonly RateLimiter? _rateLimiter;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly bool _isProduction;
    private readonly DateTime _serverStartTime;
    private readonly string _serverStartTimeRfc;
    private int _activeConnections;

    // High-performance transport: dedicated IOQueues and pooled SocketSenders
    private readonly IOQueue[] _ioQueues;
    private readonly SocketSenderPool[] _senderPools;
    private int _ioQueueIndex; // Round-robin counter
    
    private Socket? _httpListener;
    private Socket? _httpsListener;
    private Task? _httpAcceptTask;
    private Task? _httpsAcceptTask;

    // Sockets from ServerOptions.Listeners. Held separately from the two
    // built-in ports so the existing startup and shutdown paths keep their
    // shape; everything here is additive.
    private readonly List<Socket> _extraListeners = [];
    private readonly List<Task> _extraAcceptTasks = [];
#if NET10_0_OR_GREATER
    private Task? _http3AcceptTask;
#endif

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
        MiddlewarePipeline? middlewarePipeline = null,
        StaticFileHandler? staticFileHandler = null,
        OpenApiHandler? openApiHandler = null,
        RateLimiter? rateLimiter = null)
    {
        _options = options;
        _router = router;
        _serviceProvider = serviceProvider;
        _middlewarePipeline = middlewarePipeline;
        _staticFileHandler = staticFileHandler;
        _openApiHandler = openApiHandler;
        _rateLimiter = rateLimiter;
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
        
        // Create dedicated IOQueues — each is a custom PipeScheduler that batches
        // I/O continuations into a single ThreadPool work item. Connections are
        // round-robined across queues to spread load evenly.
        var ioQueueCount = IOQueue.DefaultCount;
        _ioQueues = new IOQueue[ioQueueCount];
        _senderPools = new SocketSenderPool[ioQueueCount];
        for (int i = 0; i < ioQueueCount; i++)
        {
            _ioQueues[i] = new IOQueue();
            _senderPools[i] = new SocketSenderPool(_ioQueues[i]);
        }

        // Under heavy connection fan-out (e.g. hundreds of HTTP/2 connections each multiplexing
        // ~100 streams = tens of thousands of concurrent fire-and-forget tasks), the default
        // thread-injection rate (1 thread per 500 ms) causes visible stalls during a load spike.
        // Set a floor so the pool doesn't have to ramp from cold under sudden load.
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIOThreads);
        var optimalThreads = Math.Max(Math.Max(256, Environment.ProcessorCount * 8), minWorkerThreads);
        ThreadPool.SetMinThreads(optimalThreads, Math.Max(optimalThreads, minIOThreads));
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
            _httpAcceptTask = AcceptConnectionsAsync(
                _httpListener, ListenerBinding.Plaintext(_options.HttpPort, "http"), _shutdownCts.Token);
        }

        // Start HTTPS listener
        if (_options.HttpsPort > 0 && _options.TlsOptions.Certificate != null)
        {
            _httpsListener = CreateListener(_options.HttpsPort);
            _httpsAcceptTask = AcceptConnectionsAsync(
                _httpsListener,
                ListenerBinding.Secure(_options.HttpsPort, _options.TlsOptions.Certificate, null, "https"),
                _shutdownCts.Token);

#if NET10_0_OR_GREATER
            // Start HTTP/3 listener (QUIC) on the same HTTPS port if supported
            if (QuicListener.IsSupported)
            {
                _http3AcceptTask = AcceptHttp3ConnectionsAsync(_shutdownCts.Token);
                Console.WriteLine("  quic://localhost:" + _options.HttpsPort + " (HTTP/3)");
            }
            else
            {
                Console.WriteLine("  HTTP/3 not available (QuicListener.IsSupported=false)");
            }
#endif
        }

        // Additional listeners. Each resolves its own certificate, so two of
        // them can serve different files, and each builds its own ALPN list.
        foreach (var listener in _options.Listeners)
        {
            if (listener.Port <= 0)
                continue;

            ListenerBinding binding;
            if (listener.UseTls)
            {
                listener.Tls.LoadCertificate();
                if (listener.Tls.Certificate == null)
                {
                    Console.WriteLine(
                        $"  listener '{listener.Name ?? listener.Port.ToString()}' on {listener.Port} skipped: TLS requested but no certificate resolved");
                    continue;
                }
                binding = ListenerBinding.Secure(
                    listener.Port, listener.Tls.Certificate, listener.AlpnProtocols,
                    listener.Name ?? listener.Port.ToString());
            }
            else
            {
                binding = ListenerBinding.Plaintext(listener.Port, listener.Name ?? listener.Port.ToString());
            }

            var socket = CreateListener(listener.Port);
            _extraListeners.Add(socket);
            _extraAcceptTasks.Add(AcceptConnectionsAsync(socket, binding, _shutdownCts.Token));

            var scheme = listener.UseTls ? "https" : "http";
            var alpn = listener.UseTls
                ? " [" + string.Join(", ", binding.SslOptions!.ApplicationProtocols!.Select(p => p.ToString())) + "]"
                : string.Empty;
            Console.WriteLine($"  {scheme}://localhost:{listener.Port}{alpn}");
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
        foreach (var listener in _extraListeners)
            listener.Close();

        // Wait for active connections to complete (with timeout)
        var shutdownTask = Task.WhenAll(
            [
                _httpAcceptTask ?? Task.CompletedTask,
                _httpsAcceptTask ?? Task.CompletedTask,
#if NET10_0_OR_GREATER
                _http3AcceptTask ?? Task.CompletedTask,
#endif
                .. _extraAcceptTasks,
            ]);

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

    private static void ConfigureAcceptedSocket(Socket socket)
    {
        socket.NoDelay = true;
        // Leave SO_SNDBUF and SO_RCVBUF unset so the OS autotunes both TCP windows.        
        // Autotuning grows windows for active large transfers and reclaims them from idle
        // connections, so it avoids both the throughput cliff and a flat per-connection memory cost.
    }

    private async Task AcceptConnectionsAsync(Socket listener, ListenerBinding binding, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Accept connection — no connection-limit polling, let the OS backlog handle it
                var socket = await listener.AcceptAsync(cancellationToken);
                
                ConfigureAcceptedSocket(socket);
                
                if (!_isProduction)
                    Console.WriteLine($"Accepted connection from {socket.RemoteEndPoint}");

                // Round-robin across IOQueues to spread connections evenly
                var queueIdx = (uint)Interlocked.Increment(ref _ioQueueIndex) % (uint)_ioQueues.Length;
                var ioQueue = _ioQueues[queueIdx];
                var senderPool = _senderPools[queueIdx];

                Interlocked.Increment(ref _activeConnections);
                _ = HandleConnectionAsync(socket, binding, cancellationToken, ioQueue, senderPool);
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
            }
        }
        if (!_isProduction)
            Console.WriteLine($"Accept loop exited - port: {binding.Port}, secure: {binding.IsSecure}");
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _httpListener?.Dispose();
        _httpsListener?.Dispose();
        foreach (var listener in _extraListeners)
            listener.Dispose();
        _connectionLimit.Dispose();
        _shutdownCts.Dispose();
        foreach (var pool in _senderPools)
            pool.Dispose();
    }
}
