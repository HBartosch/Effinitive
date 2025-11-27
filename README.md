# EffinitiveFramework

A high-performance C# web framework designed to outperform FastEndpoints and compete with GenHTTP.

**✅ Mission Accomplished:**
- **1.25x faster than GenHTTP** - Beating another custom HTTP server framework
- **16x faster than FastEndpoints** - Delivering on the performance promise
- **Sub-50μs response times** - The fastest C# web framework tested

## 🚀 Performance Goals

- **Zero-allocation routing** using `Span<T>` and `Memory<T>`
- **Minimal overhead** for request/response handling
- **Optimized hot paths** with aggressive inlining
- **Efficient memory management** using `ArrayPool<T>`
- **Compile-time optimization** for endpoint registration

## 📦 Features

- **Simple, intuitive API** similar to FastEndpoints
- **Type-safe endpoints** with generic request/response handling
- **Dual endpoint types** - Optimized for both sync and async I/O operations
- **Custom HTTP server** with direct socket handling for maximum performance
- **HTTP/2 support** via ALPN negotiation with binary framing and HPACK compression
- **HTTP/1.1 protocol** - Battle-tested and optimized for speed
- **TLS/HTTPS support** with configurable certificates and modern protocol support
- **Minimal allocations** in hot code paths
- **Benchmark suite** included for performance validation

## 🌐 Protocol Support

- ✅ **HTTP/1.1** - Fully supported with custom parser (sub-50μs response times)
- ✅ **HTTPS/TLS** - Full TLS 1.2/1.3 support with certificate configuration
- ✅ **HTTP/2** - Complete implementation with binary framing, HPACK, stream multiplexing, and ALPN
- ⏳ **HTTP/3/QUIC** - Under consideration

### HTTP/2 Implementation

EffinitiveFramework includes a **complete from-scratch HTTP/2 implementation**:
- **Binary framing layer** - All 9 frame types (DATA, HEADERS, SETTINGS, PING, GOAWAY, etc.)
- **HPACK compression** - Static table (61 entries) + dynamic table + Huffman encoding
- **Stream multiplexing** - Multiple concurrent requests over single TCP connection
- **Flow control** - Per-stream and connection-level window management
- **ALPN negotiation** - Automatic protocol selection during TLS handshake ("h2" or "http/1.1")
- **Settings management** - Dynamic configuration via SETTINGS frames

HTTP/2 is automatically enabled for HTTPS connections when clients negotiate it via ALPN. See [HTTP/2 Implementation Guide](docs/HTTP2_IMPLEMENTATION.md) for details.

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

**For in-memory/cached operations (use `EndpointBase`):**

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

### 2. Bootstrap the Application

```csharp
using EffinitiveFramework.Core;

var app = EffinitiveApp
    .Create(args)
    .MapEndpoints(); // Automatically discovers and registers all endpoints

await app.RunAsync();
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
│       ├── EffinitiveApp.cs             # Main application class
│       ├── Router.cs                    # High-performance router
│       ├── EndpointBase.cs              # Base endpoint class
│       ├── IEndpoint.cs                 # Endpoint interfaces
│       └── HttpMethodAttribute.cs       # HTTP method attributes
├── samples/
│   └── EffinitiveFramework.Sample/      # Sample API project
│       ├── Program.cs                   # Application entry point
│       └── Endpoints/
│           └── UserEndpoints.cs         # Example endpoints
├── benchmarks/
│   └── EffinitiveFramework.Benchmarks/  # Performance benchmarks
│       └── Program.cs                   # BenchmarkDotNet tests
└── tests/
    └── EffinitiveFramework.Tests/       # Unit tests
```

## 🔧 Development

### Prerequisites

- .NET 8 SDK or later
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

## 📊 Comparison with FastEndpoints

| Feature | EffinitiveFramework | FastEndpoints |
|---------|-------------------|---------------|
| Routing Engine | Custom zero-allocation | ASP.NET Core |
| Endpoint Definition | Class-based | Class-based |
| Request Binding | JSON deserialization | Multiple strategies |
| Performance Focus | Maximum | High |
| Dependencies | Minimal | More features |

## 🚧 Roadmap

- [ ] Route parameter extraction (e.g., `/users/{id}`)
- [ ] Query string binding
- [ ] Header/cookie binding
- [ ] Request validation
- [ ] Response caching
- [ ] OpenAPI/Swagger integration
- [ ] Middleware pipeline
- [ ] Dependency injection integration
- [ ] Response compression
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
