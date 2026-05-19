# EffinitiveFramework

![CI](https://img.shields.io/github/actions/workflow/status/HBartosch/Effinitive/ci.yml?label=CI&style=flat-square)
![Tests](https://img.shields.io/github/actions/workflow/status/HBartosch/Effinitive/ci.yml?label=Tests&style=flat-square)
[![NuGet](https://img.shields.io/nuget/v/EffinitiveFramework.Core)](https://www.nuget.org/packages/EffinitiveFramework.Core)
[![NuGet](https://img.shields.io/nuget/dt/EffinitiveFramework.Core)](https://www.nuget.org/packages/EffinitiveFramework.Core)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-blue?logo=dotnet&logoColor=white&style=flat-square)
![License](https://img.shields.io/github/license/HBartosch/Effinitive?style=flat-square)
![HTTP/2 Support](https://img.shields.io/badge/HTTP%2F2-supported-brightgreen?style=flat-square)
![HPACK Compression](https://img.shields.io/badge/HPACK-100%25%20RFC%207541-brightgreen?style=flat-square)

A high-performance C# web framework designed to outperform FastEndpoints and compete with GenHTTP.

**✅ Mission Accomplished:**
- **1.11x faster than GenHTTP** - Beating another custom HTTP server framework (46.5μs vs 51.7μs)
- **16x faster than FastEndpoints** - Delivering on the performance promise (46.5μs vs 739μs)
- **16x faster than ASP.NET Core** - Outperforming Microsoft's Minimal API (46.5μs vs 722μs)
- **Sub-50μs response times** - The fastest C# web framework tested
- **6KB allocations** - Minimal memory footprint per request

## 🚀 Performance Goals

- **Zero-allocation routing** using `Span<T>` and `Memory<T>`
- **Minimal overhead** for request/response handling
- **Optimized hot paths** with aggressive inlining
- **Efficient memory management** using `ArrayPool<T>`
- **Compile-time optimization** for endpoint registration

## 📦 Features

- **Simple, intuitive API** similar to FastEndpoints
- **Type-safe endpoints** with generic request/response handling
- **Multiple endpoint types** - Optimized for sync, async I/O, and streaming operations
  - `EndpointBase<TRequest, TResponse>` - Synchronous/cached operations (ValueTask)
  - `AsyncEndpointBase<TRequest, TResponse>` - Async I/O operations (Task)
  - `NoRequestEndpointBase<TResponse>` - Endpoints without request body (GET, health checks)
  - `NoRequestAsyncEndpointBase<TResponse>` - Async endpoints without request body
- **Server-Sent Events (SSE)** - Real-time streaming with three endpoint patterns:
  - `NoRequestSseEndpointBase` - Simple streaming without request body
  - `SseEndpointBase<TRequest>` - Streaming with request parsing
  - `SseEndpointBase<TRequest, TEventData>` - Strongly-typed event streaming
- **WebSocket support** (RFC 6455) - Full bidirectional messaging with `MapWebSocket()` or `WebSocketEndpointBase`
- **Static file serving** - Zero per-request I/O via in-memory `FrozenDictionary` cache, 25+ MIME types
- **Response compression** - Gzip middleware with single-pass serialize+compress pipeline
- **Custom HTTP server** with direct socket handling for maximum performance
- **High-performance transport layer** - `IOQueue`/`SocketSenderPool` architecture mirroring Kestrel's design
- **HTTP/2 support** via ALPN negotiation with binary framing and HPACK compression
- **HTTP/3 / QUIC** (experimental, .NET 10+) - RFC 9114 with QPACK compression, automatic alongside HTTPS
- **HTTP/1.1 protocol** - Battle-tested and optimized for speed
- **TLS/HTTPS support** with configurable certificates and modern protocol support
- **Minimal allocations** in hot code paths
- **Benchmark suite** included for performance validation

## 🌐 Protocol Support

- ✅ **HTTP/1.1** - Fully supported with custom parser (sub-50μs response times)
- ✅ **HTTPS/TLS** - Full TLS 1.2/1.3 support with certificate configuration
- ✅ **HTTP/2** - Complete implementation with binary framing, HPACK, stream multiplexing, and ALPN
- ✅ **HTTP/3/QUIC** - Experimental, .NET 10+ only (RFC 9114 with QPACK, automatic when `QuicListener.IsSupported`)
- ✅ **WebSocket** - RFC 6455, full framing/fragmentation/ping-pong/close handshake

### HTTP/2 Implementation

EffinitiveFramework includes a **complete from-scratch HTTP/2 implementation**:
- **Binary framing layer** - All 9 frame types (DATA, HEADERS, SETTINGS, PING, GOAWAY, etc.)
- **HPACK compression** - Static table (61 entries) + dynamic table + Huffman encoding
- **Stream multiplexing** - Multiple concurrent requests over single TCP connection
- **Flow control** - Per-stream and connection-level window management
- **ALPN negotiation** - Automatic protocol selection during TLS handshake ("h2" or "http/1.1")
- **Settings management** - Dynamic configuration via SETTINGS frames

HTTP/2 is automatically enabled for HTTPS connections when clients negotiate it via ALPN. See [HTTP/2 Implementation Guide](docs/HTTP2_IMPLEMENTATION.md) for details.

### HTTP/3 Implementation (.NET 10+)

When targeting .NET 10 and TLS is configured, EffinitiveFramework starts a QUIC listener on the same HTTPS port:
- **RFC 9114** - HTTP/3 framing (DATA, HEADERS, SETTINGS, GOAWAY)
- **QPACK compression** (RFC 9204) - Header encoding/decoding with static table and encoder/decoder streams
- **Control streams** - Bidirectional and unidirectional stream management
- **Automatic** - No extra configuration required; QUIC starts when `QuicListener.IsSupported` is true

### WebSocket Implementation (RFC 6455)

- **Full frame support** - Text, Binary, Ping, Pong, Close, Continuation frames
- **Fragmentation** - Multi-frame messages transparently reassembled
- **Keep-alive** - Automatic Pong replies to client Ping frames
- **Close handshake** - Graceful connection termination with status codes
- **Fluent registration** - `MapWebSocket(path, handler)` or subclass `WebSocketEndpointBase`

## 🏗️ Architecture

### Core Components

1. **EffinitiveApp** - Main application bootstrap
2. **Router** - High-performance routing engine using zero-allocation techniques
3. **EndpointBase<TRequest, TResponse>** - Base class for synchronous/cached operations (ValueTask)
4. **AsyncEndpointBase<TRequest, TResponse>** - Base class for I/O operations (Task)
5. **IEndpoint** - Core endpoint interfaces

### Performance Optimizations

- **Span-based routing** - Routes are matched using `ReadOnlySpan<char>` to avoid string allocations
- **Smart async handling** - `ValueTask<T>` for sync operations, `Task<T>` for I/O
- **Struct types** - Key data structures use structs where appropriate
- **ArrayPool** - Reuses arrays for temporary operations
- **Unsafe blocks enabled** - Allows for low-level optimizations where needed

## 📖 Quick Start

### 1. Create an Endpoint

**For simple GET endpoints without request body (use `NoRequestEndpointBase`):**

```csharp
using EffinitiveFramework.Core;

public class HealthCheckEndpoint : NoRequestEndpointBase<HealthResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/health";

    public override ValueTask<HealthResponse> HandleAsync(
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new HealthResponse 
        { 
            Status = "Healthy",
            Timestamp = DateTime.UtcNow,
            Version = "2.0.0"
        });
    }
}
```

**For in-memory/cached operations with request body (use `EndpointBase`):**

```csharp
using EffinitiveFramework.Core;

public class GetUsersEndpoint : EndpointBase<EmptyRequest, UsersResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/users";

    public override ValueTask<UsersResponse> HandleAsync(
        EmptyRequest request, 
        CancellationToken cancellationToken = default)
    {
        var users = new List<User>
        {
            new User { Id = 1, Name = "Alice", Email = "alice@example.com" },
            new User { Id = 2, Name = "Bob", Email = "bob@example.com" }
        };

        return ValueTask.FromResult(new UsersResponse { Users = users });
    }
}
```

**For database/I/O operations (use `AsyncEndpointBase`):**

```csharp
using EffinitiveFramework.Core;

public class CreateUserEndpoint : AsyncEndpointBase<CreateUserRequest, UserResponse>
{
    protected override string Method => "POST";
    protected override string Route => "/api/users";

    public override async Task<UserResponse> HandleAsync(
        CreateUserRequest request, 
        CancellationToken cancellationToken = default)
    {
        // True async I/O - database insert
        var user = await _dbContext.Users.AddAsync(new User 
        { 
            Name = request.Name, 
            Email = request.Email 
        }, cancellationToken);
        
        await _dbContext.SaveChangesAsync(cancellationToken);
        
        return new UserResponse { User = user, Success = true };
    }
}
```

**For real-time Server-Sent Events streaming (use SSE endpoints):**

```csharp
using EffinitiveFramework.Core.Http.ServerSentEvents;

public class ServerTimeEndpoint : NoRequestSseEndpointBase
{
    protected override string Method => "GET";
    protected override string Route => "/api/stream/time";

    protected override async Task HandleStreamAsync(
        SseStream stream, 
        CancellationToken cancellationToken)
    {
        // Start automatic keep-alive pings
        _ = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(15), cancellationToken);
        
        await stream.WriteAsync("connected", "Server time stream started");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var timeData = new { Time = DateTime.UtcNow, Zone = "UTC" };
            await stream.WriteJsonAsync(timeData, cancellationToken);
            await Task.Delay(1000, cancellationToken);
        }
    }
}
```

**Define your DTOs:**

```csharp
public record UsersResponse
{
    public List<User> Users { get; init; } = new();
}

public record User
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}
```

> 💡 **See [Endpoint Selection Guide](docs/EndpointSelectionGuide.md) for detailed guidance on choosing between `EndpointBase` and `AsyncEndpointBase`**

**For WebSocket connections (use `MapWebSocket` or subclass `WebSocketEndpointBase`):**

```csharp
using EffinitiveFramework.Core.WebSocket;

// Inline handler
app.MapWebSocket("/ws/echo", async (conn, ct) =>
{
    while (conn.IsOpen)
    {
        var msg = await conn.ReceiveAsync(ct);
        if (msg == null) break;
        await conn.SendAsync(msg.Value.Data, msg.Value.Type, ct);
    }
});

// Class-based handler
public class EchoEndpoint : WebSocketEndpointBase
{
    public override string Route => "/ws/echo";

    public override async Task OnConnectedAsync(
        WebSocketConnection connection, CancellationToken cancellationToken)
    {
        while (connection.IsOpen)
        {
            var msg = await connection.ReceiveAsync(cancellationToken);
            if (msg == null) break;
            await connection.SendAsync(msg.Value.Data, msg.Value.Type, cancellationToken);
        }
    }
}
```

### 2. Bootstrap the Application

```csharp
using EffinitiveFramework.Core;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

// Create the app, configure ports, TLS, services and endpoints, then build
var app = EffinitiveApp
    .Create()
    .UsePort(5000)           // HTTP on port 5000
    .UseHttpsPort(5001)      // HTTPS on port 5001 (HTTP/2 + HTTP/3 via ALPN/QUIC)
    .ConfigureTls(tls =>
    {
        tls.CertificatePath = "localhost.pfx";
        tls.CertificatePassword = "dev-password";
    })
    .UseResponseCompression()            // Enable gzip for supported clients
    .UseStaticFiles("wwwroot")           // Serve files from ./wwwroot at /static
    .MapWebSocket("/ws", async (conn, ct) =>   // WebSocket endpoint
    {
        while (conn.IsOpen)
        {
            var msg = await conn.ReceiveAsync(ct);
            if (msg != null) await conn.SendAsync(msg.Value.Data, msg.Value.Type, ct);
        }
    })
    .MapEndpoints() // Automatically discovers and registers all endpoints
    .Build();

// Run the server until cancelled
await app.RunAsync(cts.Token);
```

### Dependency Injection (Configure services)

EffinitiveFramework exposes a light-weight DI integration via `ConfigureServices` on the builder. Use it to register DbContexts, services and middleware dependencies.

```csharp
var app = EffinitiveApp.Create()
    .ConfigureServices(services =>
    {
        // Register a scoped EF Core DbContext
        services.AddScoped<AppDbContext>(sp =>
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=products.db")
                .Options;
            return new AppDbContext(options);
        });

        // Register application services
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IOrderService, OrderService>();
    })
    .MapEndpoints(typeof(Program).Assembly)
    .Build();

// Resolve a scope for initialization or background work
using var scope = ((EffinitiveFramework.Core.DependencyInjection.ServiceProvider)app.Services!).CreateScope();
var ctx = scope.ServiceProvider.GetService<AppDbContext>();
```

### 3. Run the Application

```bash
dotnet run --project samples/EffinitiveFramework.Sample
```

The API will be available at `http://localhost:5000` (or as configured).

## 🧪 Running Benchmarks

```bash
dotnet run --project benchmarks/EffinitiveFramework.Benchmarks -c Release
```

The benchmark suite compares:
- Route matching performance
- Endpoint invocation overhead
- Memory allocations
- Request/response throughput

## 🎯 Benchmark Results

### Framework Comparison (HTTP End-to-End)

| Framework                   | GET Mean  | POST Mean | vs EffinitiveFramework |
|----------------------------|-----------|-----------|------------------------|
| **EffinitiveFramework**    | **44.37 μs** | **44.89 μs** | **Baseline** |
| GenHTTP                    | 54.58 μs  | 57.04 μs  | 1.23-1.27x slower |
| FastEndpoints              | 726.72 μs | 725.10 μs | **16.2-16.4x slower** |
| ASP.NET Core Minimal API   | 725.19 μs | 715.01 μs | **15.9-16.4x slower** |

### Key Performance Metrics

✅ **Fastest C# web framework tested**  
✅ **1.23-1.27x faster than GenHTTP** (another custom HTTP server)  
✅ **~16x faster than FastEndpoints** and ASP.NET Core Minimal API  
✅ **Sub-50μs response times** for both GET and POST  
✅ **4.5-5.5 KB memory** per request (minimal allocations)  

**See [BENCHMARK_RESULTS.md](BENCHMARK_RESULTS.md) for detailed results and analysis.**

## 📚 Project Structure

```
EffinitiveFramework/
├── src/
│   └── EffinitiveFramework.Core/       # Core framework library
│       ├── EffinitiveApp.cs             # Main application class + fluent builder
│       ├── Router.cs                    # High-performance router (FrozenDictionary)
│       ├── EndpointBase.cs              # Base endpoint classes
│       ├── IEndpoint.cs                 # Endpoint interfaces
│       ├── Http/                        # HTTP/1.1 parsing and response writing
│       ├── Http2/                       # HTTP/2 framing, HPACK, stream multiplexing
│       ├── Http3/                       # HTTP/3 / QUIC + QPACK (.NET 10+ only)
│       ├── WebSocket/                   # RFC 6455 WebSocket framing and endpoints
│       ├── StaticFiles/                 # In-memory static file handler
│       ├── Transport/                   # IOQueue, SocketSenderPool, DuplexPipe
│       ├── Middleware/                  # Pipeline + ResponseCompressionMiddleware
│       ├── Authentication/              # JWT, API Key, custom auth handlers
│       ├── Authorization/               # [Authorize], [AllowAnonymous] attributes
│       ├── DependencyInjection/         # ServiceCollection + ServiceProvider
│       └── Configuration/              # ServerOptions, TlsOptions
├── samples/
│   └── EffinitiveFramework.Sample/      # Sample API project
│       ├── Program.cs                   # Application entry point
│       └── Endpoints/                   # Example endpoints
├── benchmarks/
│   └── EffinitiveFramework.Benchmarks/  # Performance benchmarks (BenchmarkDotNet)
└── tests/
    └── EffinitiveFramework.Tests/       # Unit tests
```

## 🔧 Development

### Prerequisites

- .NET 8 SDK or later (.NET 10 SDK for HTTP/3 support)
- Visual Studio 2022 / VS Code / Rider

### Build

```bash
dotnet build
```

### Run Tests

```bash
dotnet test
```

### Run Sample

```bash
dotnet run --project samples/EffinitiveFramework.Sample
```

## 🎨 Design Principles

1. **Performance First** - Every feature is evaluated for its performance impact
2. **Zero Allocations** - Hot paths should allocate as little as possible
3. **Simple API** - Easy to use, hard to misuse
4. **Type Safety** - Leverage C# type system for compile-time guarantees
5. **Minimal Dependencies** - Only depend on ASP.NET Core fundamentals

## 🔬 Performance Techniques Used

- **Span<T> and Memory<T>** - For zero-copy string operations
- **ArrayPool<T>** - For temporary buffer allocations
- **ValueTask<T>** - For reduced async allocations
- **Aggressive Inlining** - `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- **Struct Types** - Value types for small, frequently-used data
- **Unsafe Code** - Low-level optimizations where beneficial

## 🆕 What's New in v2.0.0

| Feature | Details |
|---|---|
| WebSocket | RFC 6455 — `MapWebSocket()` or `WebSocketEndpointBase`. Fragmentation, ping/pong, close handshake. |
| HTTP/3 / QUIC | RFC 9114 + QPACK (RFC 9204). Auto-starts on .NET 10 alongside HTTPS. |
| Static files | Pre-loads `wwwroot` into `FrozenDictionary` at startup — zero per-request I/O. |
| Gzip compression | `UseResponseCompression()` — single-pass serialize+compress via pooled buffers. |
| Transport layer | `IOQueue`/`SocketSenderPool` matching Kestrel's architecture for maximum throughput. |
| Dual-target package | Ships `net8.0` + `net10.0` targets in a single NuGet package. |

See [RELEASE_NOTES_v2.0.0.md](RELEASE_NOTES_v2.0.0.md) for the full release notes and [CHANGELOG.md](CHANGELOG.md) for the complete version history.

## 📊 Comparison with FastEndpoints

| Feature | EffinitiveFramework | FastEndpoints |
|---------|-------------------|---------------|
| Routing Engine | Custom zero-allocation | ASP.NET Core |
| Endpoint Definition | Class-based | Class-based |
| Request Binding | JSON deserialization | Multiple strategies |
| Performance Focus | Maximum | High |
| Dependencies | Minimal | More features |

## 🚧 Roadmap

- [x] ~~Route parameter extraction (e.g., `/users/{id}`)~~ ✅ **IMPLEMENTED**
- [x] ~~Query string binding~~ ✅ **IMPLEMENTED** (API Key auth)
- [x] ~~Header/cookie binding~~ ✅ **IMPLEMENTED** (Auth handlers)
- [x] ~~Request validation~~ ✅ **IMPLEMENTED** (Routya.ResultKit integration)
- [x] ~~Middleware pipeline~~ ✅ **IMPLEMENTED** (High-performance pipeline)
- [x] ~~Dependency injection integration~~ ✅ **IMPLEMENTED** (Full DI support)
- [x] ~~Server-Sent Events (SSE)~~ ✅ **IMPLEMENTED v1.1.0** (Real-time streaming)
- [x] ~~Response compression (gzip)~~ ✅ **IMPLEMENTED v2.0.0** (Single-pass serialize+compress)
- [x] ~~WebSocket support~~ ✅ **IMPLEMENTED v2.0.0** (RFC 6455, full framing)
- [x] ~~Static file serving~~ ✅ **IMPLEMENTED v2.0.0** (Zero per-request I/O)
- [x] ~~HTTP/3 / QUIC protocol~~ ✅ **IMPLEMENTED v2.0.0** (Experimental, .NET 10+)
- [ ] Response caching
- [ ] OpenAPI/Swagger integration
- [ ] Rate limiting

## 🤝 Contributing

Contributions are welcome! Please ensure:
- All benchmarks pass with improved or comparable performance
- Code follows existing patterns
- Tests are included for new features
- Performance-critical code includes comments explaining optimizations

## 📄 License

MIT License - see LICENSE file for details

## 🙏 Acknowledgments

- Inspired by FastEndpoints
- Performance techniques from GenHTTP
- Built on ASP.NET Core

---

**Note**: This is a performance-focused framework. Always benchmark your specific use case to ensure it meets your requirements.
