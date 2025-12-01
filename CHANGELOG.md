# Changelog

All notable changes to EffinitiveFramework will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2025-11-28

### Added
- **Server-Sent Events (SSE) streaming support** - Complete implementation for real-time event streaming
  - `NoRequestSseEndpointBase` - Simple streaming without request body
  - `SseEndpointBase<TRequest>` - Streaming with request parsing
  - `SseEndpointBase<TRequest, TEventData>` - Strongly-typed event streaming
  - `SseEvent` class for W3C-compliant event formatting
  - `SseStream` with automatic keep-alive support
  - `TypedSseStream<T>` for compile-time type safety
- **NoRequest endpoint variants** - Cleaner API for endpoints without request bodies
  - `NoRequestEndpointBase<TResponse>` - Synchronous/cached operations (ValueTask)
  - `NoRequestAsyncEndpointBase<TResponse>` - Async I/O operations (Task)
- **ContentType customization** - Protected virtual `ContentType` property on all endpoint base classes
  - Override to return custom content types (text/plain, text/html, application/xml, etc.)
  - Works with all endpoint types (NoRequest, regular, async, NoRequestAsync)
- **Sample endpoints demonstrating new features:**
  - `ServerTimeStreamEndpoint` - Real-time SSE time updates
  - `StockPriceStreamEndpoint` - Strongly-typed SSE events
  - `HealthCheckEndpoint` - NoRequest health check
  - `DatabaseStatsEndpoint` - NoRequestAsync with simulated DB query
  - `PlainTextEndpoint` - Custom ContentType (text/plain)
  - `HtmlEndpoint` - Custom ContentType (text/html)

### Documentation
- Added `SSE_ServerSentEvents.md` - Complete SSE implementation guide
- Added `EmptyEndpoints_Feature.md` - NoRequest endpoint variants documentation
- Updated `EndpointSelectionGuide.md` - Added NoRequest endpoint guidance
- Updated `README.md` - Version 1.1.0 features and examples

### Tests
- Added 11 SSE-specific tests (event formatting, streaming, keep-alive)
- Added 6 NoRequest endpoint tests (including ContentType customization)
- Total test count: 71 tests (all passing)

### Changed
- Enhanced endpoint base classes with ContentType property
- Updated package tags to include SSE and streaming

## [1.0.0] - 2025-11-26

### Security - ✅ ALL CRITICAL ISSUES FIXED (November 26, 2025)
- ✅ **FIXED:** HTTP/2 frame size validation enforced (prevents 100MB frame DoS)
- ✅ **FIXED:** HTTP/1.1 Content-Length limit (default 30MB, prevents 2GB body DoS)
- ✅ **FIXED:** HTTP/2 header list size enforcement (prevents header flooding)
- ✅ **FIXED:** Request timeout mechanism (prevents Slowloris attacks)
- ✅ **FIXED:** HTTP/2 concurrent streams limit enforcement (prevents stream flooding)
- ✅ **FIXED:** HPACK decompression bomb protection (tracks decompressed size)
- ✅ **FIXED:** HTTP/2 settings validation (RFC 7540 range enforcement)

**Security Grade: A** - Production-ready

## [1.0.0-rc.1] - TBD (Next Release)

### Added
- Ultra-fast HTTP/1.1 server (16x faster than FastEndpoints, 1.25x faster than GenHTTP)
- Full HTTP/2 support with binary framing and multiplexing
- ALPN negotiation for automatic protocol selection
- HPACK header compression (static table + dynamic table + Huffman encoding)
- TLS/HTTPS support with X.509 certificate validation
- JWT Bearer authentication handler
- API Key authentication handler
- Custom authentication handler support
- Role-based access control (RBAC) with `[Authorize(Roles="...")]`
- Automatic request validation via Routya.ResultKit integration
- RFC 7807 ProblemDetails error responses
- Dependency Injection (scoped, singleton, transient lifetimes)
- Middleware pipeline with builder pattern
- Entity Framework Core integration sample
- Route parameters (`/users/{id}`)
- CORS middleware
- Connection pooling and buffer pooling (zero-allocation design)
- Comprehensive benchmarks with BenchmarkDotNet

### Performance
- **41-48μs** response time (empty GET)
- **16.2x faster** than FastEndpoints
- **1.25x faster** than GenHTTP
- **15.4x faster** than ASP.NET Core Minimal API
- Zero-allocation hot paths with Span<T>, Memory<T>, and ArrayPool
- HTTP/2 sub-50μs response times

### Documentation
- Complete API documentation in `docs/`
- Authentication/Authorization guide
- Validation integration guide
- HTTP/2 implementation details
- Endpoint selection guide
- Benchmark results and methodology

### Known Issues
- ⚠️ Security issues listed above MUST be fixed before production use
- Rate limiting not yet implemented
- Response compression not yet implemented
- OpenAPI/Swagger not yet implemented
- File upload/download not yet implemented
- WebSockets not yet implemented

---

## [1.0.0] - TBD (Future Release)

### Planned Features
- All critical security issues resolved
- Request timeout mechanism
- Response compression (Gzip/Brotli)
- Rate limiting middleware
- Response caching
- File upload/download support
- OpenAPI/Swagger generation
- Model binding from query/headers/cookies
- Static file serving
- Health checks endpoint
- Metrics and telemetry

### Breaking Changes
- None (first major release)

---

## Version History

- **[Unreleased]** - Current development branch
- **[0.9.0-beta.1]** - First public beta (planned)
- **[1.0.0]** - Production release (planned after security fixes)

---

## How to Upgrade

### From Pre-release to 1.0.0

Breaking changes TBD based on beta feedback.

---

## Contributors

- Your Name (@yourusername) - Creator and maintainer

---

## Links

- **NuGet Package**: https://www.nuget.org/packages/EffinitiveFramework.Core (not yet published)
- **GitHub Repository**: https://github.com/yourusername/EffinitiveFramework
- **Documentation**: https://github.com/yourusername/EffinitiveFramework/tree/main/docs
- **Benchmarks**: [BENCHMARK_RESULTS.md](BENCHMARK_RESULTS.md)
- **Security**: [SECURITY_ASSESSMENT.md](SECURITY_ASSESSMENT.md)
