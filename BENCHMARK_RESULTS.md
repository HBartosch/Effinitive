# EffinitiveFramework Benchmark Results

## Framework Comparison - HTTP End-to-End Performance

**Test Environment:**
- CPU: AMD Ryzen 7 3700X 3.60GHz (8 cores, 16 threads)
- OS: Windows 11 (24H2)
- Runtime: .NET 8.0.15
- BenchmarkDotNet: v0.15.6
- Protocol: HTTP/1.1 and HTTP/2 (ALPN negotiated for HTTPS)

### Summary

EffinitiveFramework **outperforms GenHTTP, FastEndpoints, and ASP.NET Core Minimal API** by a significant margin:

| Method                              | Mean      | Ratio vs Effinitive | Allocated | Alloc Ratio |
|------------------------------------|-----------|---------------------|-----------|-------------|
| **GET - EffinitiveFramework (Sync)**  | **43.22 μs** | **1.00x (baseline)** | **4.59 KB** | **1.00x** |
| **GET - EffinitiveFramework (Async)** | **41.92 μs** | **0.97x (3% faster!)** | **4.64 KB** | **1.01x** |
| GET - GenHTTP                         | 53.16 μs  | 1.23x slower        | 4.45 KB   | 0.97x       |
| GET - FastEndpoints                   | 721.65 μs | 16.70x slower       | 7.12 KB   | 1.55x       |
| GET - ASP.NET Core Minimal            | 723.33 μs | 16.74x slower       | 6.22 KB   | 1.35x       |
| **POST - EffinitiveFramework (Sync)** | **45.68 μs** | **1.00x (baseline)** | **5.51 KB** | **1.00x** |
| **POST - EffinitiveFramework (Async)**| **47.69 μs** | **1.04x (4% slower)** | **5.56 KB** | **1.01x** |
| POST - GenHTTP                        | 58.50 μs  | 1.28x slower        | 5.43 KB   | 0.98x       |
| POST - FastEndpoints                  | 716.48 μs | 15.68x slower       | 8.12 KB   | 1.47x       |
| POST - ASP.NET Core Minimal           | 681.95 μs | 14.93x slower       | 7.49 KB   | 1.36x       |

### Key Findings

1. **Async vs Sync Performance**:
   - **EffinitiveFramework's async endpoints are comparable to sync** with only 3-4% difference
   - GET requests: **Async is 3% faster** (41.92 μs vs 43.22 μs)
   - POST requests: **Async is 4% slower** (47.69 μs vs 45.68 μs)
   - This demonstrates the framework's efficient handling of both `ValueTask<T>` (sync) and `Task<T>` (async) patterns
   
2. **Performance Advantage vs Custom HTTP Servers**:
   - **EffinitiveFramework vs GenHTTP**: EffinitiveFramework is **1.23-1.28x faster** (19-28% faster)
     - Both frameworks use custom HTTP parsing with System.IO.Pipelines
   
2. **Performance Advantage vs Custom HTTP Servers**:
   - **EffinitiveFramework vs GenHTTP**: EffinitiveFramework is **1.23-1.28x faster** (19-28% faster)
     - Both frameworks use custom HTTP parsing with System.IO.Pipelines
     - EffinitiveFramework's aggressive optimizations give it the edge
   
3. **Performance Advantage vs Traditional Frameworks**: EffinitiveFramework is **~16x faster** than FastEndpoints and ASP.NET Core
   - GET requests: 41-43 μs vs 681-723 μs
   - POST requests: 45-48 μs vs 681-716 μs

4. **Memory Efficiency**: 
   - **vs GenHTTP**: Comparable memory usage (EffinitiveFramework uses 1-3% more)
   - **vs FastEndpoints/ASP.NET Core**: 26-35% less memory allocation per request
   - **Async overhead**: Only 1% additional memory for async endpoints

5. **Consistency**: Lower standard deviation shows more predictable performance
   - EffinitiveFramework Sync GET: ±0.54 μs
   - EffinitiveFramework Async GET: ±0.74 μs
   - EffinitiveFramework Sync POST: ±0.67 μs
   - EffinitiveFramework Async POST: ±1.39 μs

6. **GC Pressure**: Minimal GC Gen0 collections
   - Sync: 0.49-0.61 per 1000 ops
   - Async: 0.49-0.61 per 1000 ops (same as sync!)

### Architecture Benefits

The performance advantage comes from:

1. **Custom HTTP/1.1 & HTTP/2 Server**: Direct socket handling without ASP.NET Core overhead
   - Custom HTTP/1.1 parser optimized for speed
   - Complete HTTP/2 implementation with binary framing, HPACK compression, and stream multiplexing
   - ALPN negotiation for automatic protocol selection
2. **Zero-Allocation Patterns**: 
   - System.IO.Pipelines for I/O
   - Span<T> and Memory<T> for parsing
   - Pre-cached HTTP response headers
   - ObjectPool for connection reuse
3. **Optimized Routing**: Custom zero-allocation router (37.7 ns average)
4. **Dual Endpoint Architecture**:
   - ValueTask<T> for synchronous operations (27.29 ns)
   - Task<T> for async I/O (43.17 ns)

### Comparison to Project Goals

✅ **Faster than FastEndpoints**: **16x improvement achieved**  
✅ **Competitive with GenHTTP**: **1.23-1.27x faster** - outperforming another custom HTTP server framework  
✅ **Zero-allocation design**: Minimal memory allocations and GC pressure  

### Why EffinitiveFramework is Faster Than GenHTTP

Both frameworks use custom HTTP parsing with System.IO.Pipelines, but EffinitiveFramework edges ahead through:
- **More aggressive inlining** and hot-path optimizations
- **Pre-cached response headers** reducing allocations
- **Optimized endpoint invocation** (27-43 ns vs GenHTTP's routing overhead)
- **Tighter integration** between router and endpoint execution

## Detailed Results

Full benchmark results are available in:
- `BenchmarkDotNet.Artifacts/results/EffinitiveFramework.Benchmarks.FrameworkComparisonBenchmarks-report.html`
- `BenchmarkDotNet.Artifacts/results/EffinitiveFramework.Benchmarks.FrameworkComparisonBenchmarks-report-github.md`
- `BenchmarkDotNet.Artifacts/results/EffinitiveFramework.Benchmarks.FrameworkComparisonBenchmarks-report.csv`

## Component Benchmarks

### Router Performance
- Route matching: **37.72 ns** (mean)
- Zero allocations using Span<char>

### Direct Endpoint Invocation
- Synchronous endpoint (ValueTask): **27.29 ns** (184 B allocated)
- Async endpoint (Task): **43.17 ns** (328 B allocated)

## Conclusion

EffinitiveFramework successfully achieves its goals:

1. ✅ **Faster than FastEndpoints**: **16x performance improvement** over FastEndpoints
2. ✅ **Competitive with GenHTTP**: **1.23-1.27x faster** than GenHTTP, another custom HTTP server framework

The framework's zero-allocation design and optimized components deliver:
- **Sub-50μs response times** for both GET and POST requests
- **~16x better performance** vs FastEndpoints and ASP.NET Core Minimal APIs
- **~1.25x better performance** vs GenHTTP (another custom server)
- **Minimal memory allocations** (4.5-5.5 KB per request)
- **Predictable latency** with low standard deviation

EffinitiveFramework is the **fastest C# web framework tested**, making it an excellent choice for high-performance scenarios where latency and throughput are critical, such as microservices, real-time APIs, and high-load web services.

