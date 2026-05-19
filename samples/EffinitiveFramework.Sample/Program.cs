using System.IO.Compression;
using EffinitiveFramework.Core;
using EffinitiveFramework.Sample.Endpoints.WebSocket;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Build the app ──────────────────────────────────────────────────────────
var app = EffinitiveApp
    .Create()

    // Ports
    .UsePort(5000)          // HTTP/1.1 and WebSocket
    // Uncomment to enable HTTPS + HTTP/2 (via ALPN) + HTTP/3 (on .NET 10):
    // .UseHttpsPort(5001)
    // .ConfigureTls(tls =>
    // {
    //     tls.CertificatePath = "localhost.pfx";
    //     tls.CertificatePassword = "dev-password";
    // })

    // Server options
    .Configure(options =>
    {
        options.EnableDebugLogging = false;
        options.MaxConcurrentConnections = Environment.ProcessorCount * 200;
        options.HeaderTimeout = TimeSpan.FromSeconds(10);
        options.RequestTimeout = TimeSpan.FromSeconds(30);
        options.IdleTimeout = TimeSpan.FromSeconds(60);
    })

    // ── v2.0: Response compression ─────────────────────────────────────────
    // Gzip-compresses JSON, text, HTML, JS, and XML responses when the client
    // sends Accept-Encoding: gzip. Responses smaller than 1 KB are not compressed.
    .UseResponseCompression(
        compressionLevel: CompressionLevel.Fastest,
        minimumSize: 1024)

    // ── v2.0: Static files ─────────────────────────────────────────────────
    // Pre-loads all files from ./wwwroot into memory at startup.
    // Served under /static — e.g. /static/index.html, /static/demo.css
    .UseStaticFiles("wwwroot")

    // ── v2.0: WebSocket — Echo server ──────────────────────────────────────
    // Class-based handler: subclass WebSocketEndpointBase and pass via delegate.
    .MapWebSocket("/ws/echo",
        (conn, ct) => new EchoWebSocketEndpoint().OnConnectedAsync(conn, ct))

    // ── v2.0: WebSocket — Live metrics push ────────────────────────────────
    // Pushes GC memory, thread-pool stats, and uptime to the client every second.
    .MapWebSocket("/ws/metrics",
        (conn, ct) => new LiveMetricsWebSocketEndpoint().OnConnectedAsync(conn, ct))

    // ── v2.0: Inline WebSocket handler ─────────────────────────────────────
    // MapWebSocket also accepts a plain lambda — no class needed for simple cases.
    .MapWebSocket("/ws/ping",
        async (conn, ct) =>
        {
            await conn.SendAsync(
                "pong"u8.ToArray(),
                EffinitiveFramework.Core.WebSocket.WebSocketMessageType.Text,
                ct);
        })

    // Auto-discover and register all HTTP endpoints in this assembly
    .MapEndpoints()
    .Build();

// ── Print routes ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("EffinitiveFramework v2.0 — Feature Demo");
Console.WriteLine("=========================================");
Console.WriteLine("HTTP endpoints:");
Console.WriteLine("  GET  http://localhost:5000/api/health");
Console.WriteLine("  GET  http://localhost:5000/api/users");
Console.WriteLine("  POST http://localhost:5000/api/users");
Console.WriteLine("  GET  http://localhost:5000/api/stream/time   (SSE)");
Console.WriteLine();
Console.WriteLine("WebSocket endpoints:");
Console.WriteLine("  ws://localhost:5000/ws/echo     — RFC 6455 echo server");
Console.WriteLine("  ws://localhost:5000/ws/metrics  — live GC/thread-pool push");
Console.WriteLine("  ws://localhost:5000/ws/ping     — inline lambda handler");
Console.WriteLine();
Console.WriteLine("Static files (served from ./wwwroot, pre-loaded into memory):");
Console.WriteLine("  http://localhost:5000/static/index.html  — feature demo page");
Console.WriteLine("  http://localhost:5000/static/demo.css");
Console.WriteLine();
Console.WriteLine("Features active:");
Console.WriteLine("  ✓ Gzip response compression (threshold: 1 KB)");
Console.WriteLine("  ✓ Static file serving from wwwroot/");
Console.WriteLine("  ✓ WebSocket support (RFC 6455)");
Console.WriteLine("  ✓ Server-Sent Events (SSE)");
Console.WriteLine("  ✓ HTTP/2 (enable HTTPS port to activate via ALPN)");
#if NET10_0_OR_GREATER
Console.WriteLine("  ✓ HTTP/3 / QUIC available (enable HTTPS port to activate)");
#endif
Console.WriteLine();
Console.WriteLine("Open http://localhost:5000/static/index.html in your browser");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

await app.RunAsync(cts.Token);
