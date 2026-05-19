# Release Notes - v2.0.0

**Release Date:** May 18, 2026

## Overview

v2.0.0 is a major release that delivers the four most-requested features from the roadmap: **WebSockets**, **HTTP/3/QUIC**, **static file serving**, and **response compression**. It also introduces a high-performance transport layer modelled on Kestrel's architecture and adds dual-target support for .NET 10 alongside .NET 8.

---

## New Features

### WebSocket Support (RFC 6455)

Full RFC 6455 WebSocket implementation with zero-copy framing, automatic ping/pong handling, message fragmentation reassembly, and a graceful close handshake.

**Core types:**
- `WebSocketConnection` — bidirectional message channel with `ReceiveAsync` / `SendAsync` / `SendCloseAsync`
- `WebSocketEndpointBase` — class-based endpoint, override `OnConnectedAsync`
- `MapWebSocket()` — fluent inline handler registration

```csharp
// Inline handler
var app = EffinitiveApp.Create()
    .MapWebSocket("/ws/echo", async (conn, ct) =>
    {
        while (conn.IsOpen)
        {
            var msg = await conn.ReceiveAsync(ct);
            if (msg == null) break;
            await conn.SendAsync(msg.Value.Data, msg.Value.Type, ct);
        }
    })
    .Build();

// Class-based handler
public class ChatEndpoint : WebSocketEndpointBase
{
    public override string Route => "/ws/chat";

    public override async Task OnConnectedAsync(
        WebSocketConnection connection, CancellationToken cancellationToken)
    {
        await connection.SendAsync(
            "Welcome!"u8.ToArray(), WebSocketMessageType.Text, cancellationToken);

        while (connection.IsOpen)
        {
            var msg = await connection.ReceiveAsync(cancellationToken);
            if (msg == null) break;
            // Broadcast logic here
        }
    }
}
```

**Protocol details:**
- Frame types: Text, Binary, Ping, Pong, Close, Continuation
- Fragmented messages transparently reassembled
- Unsolicited Pong frames ignored (RFC 6455 §5.5.3)
- `PipeReader`/`PipeWriter` backed for high-throughput I/O
- Pre-allocated 65 KB write buffer per connection; larger payloads auto-allocate

---

### HTTP/3 / QUIC (.NET 10+, RFC 9114)

HTTP/3 over QUIC transport is now available when running on .NET 10. The framework starts a QUIC listener on the same HTTPS port automatically when `QuicListener.IsSupported` is true.

**Features:**
- Full RFC 9114 HTTP/3 frame handling (DATA, HEADERS, SETTINGS, GOAWAY)
- QPACK header compression (RFC 9204) — static table + encoder/decoder streams
- Unidirectional control streams (control, QPACK encoder, QPACK decoder)
- GOAWAY for graceful connection termination
- Maximum 256 concurrent streams per connection

```csharp
var app = EffinitiveApp.Create()
    .UseHttpsPort(5001)
    .ConfigureTls(tls =>
    {
        tls.CertificatePath = "localhost.pfx";
        tls.CertificatePassword = "dev-password";
    })
    .MapEndpoints()
    .Build();

// HTTP/3 starts automatically on .NET 10 when TLS is configured:
// https://localhost:5001  (HTTP/1.1 + HTTP/2 via ALPN)
// quic://localhost:5001   (HTTP/3)
```

**Target framework note:** HTTP/3 types are compiled only for `net10.0`. The `net8.0` target omits the `Http3/` directory entirely; no API surface change.

---

### Static File Serving

Files are pre-loaded from disk into a `FrozenDictionary<string, CachedStaticFile>` at startup. Every subsequent request is served from memory with a single dictionary lookup — zero per-request I/O, zero allocations on the hot path.

```csharp
var app = EffinitiveApp.Create()
    // Serve files from ./wwwroot at URL prefix /static
    .UseStaticFiles("wwwroot")

    // Or with full options
    .UseStaticFiles(options =>
    {
        options.RootPath = "public";
        options.RequestPath = "/assets";
        options.CacheControl = "public, max-age=86400";
    })
    .Build();
```

**Supported MIME types (25+):** HTML, CSS, JS, JSON, PNG, JPEG, GIF, SVG, WebP, ICO, TXT, XML, WOFF/WOFF2/TTF/OTF/EOT, WASM, PDF, ZIP, and generic `application/octet-stream`.

Options:

| Property | Default | Description |
|---|---|---|
| `RootPath` | `"wwwroot"` | Directory on disk |
| `RequestPath` | `"/static"` | URL path prefix |
| `CacheControl` | `"public, max-age=3600"` | `Cache-Control` header value |

---

### Response Compression (Gzip)

`ResponseCompressionMiddleware` inspects the `Accept-Encoding` request header and marks eligible responses for gzip compression. The actual compression happens inside `HttpResponseWriter` as part of the existing JSON-serialization pipeline — a single pooled-buffer pass covering both serialize and compress steps.

```csharp
var app = EffinitiveApp.Create()
    // Default: CompressionLevel.Fastest, minimum 1024 bytes
    .UseResponseCompression()

    // Or custom settings
    .UseResponseCompression(
        compressionLevel: CompressionLevel.Optimal,
        minimumSize: 512)
    .Build();
```

**Behaviour:**
- Only compresses when client sends `Accept-Encoding: gzip`
- Skips responses already carrying a `Content-Encoding` header
- Skips streaming (`IsStreaming`) responses
- Default compressible types: `application/json`, `text/*`, `application/javascript`, `application/xml`
- Sets `Content-Encoding: gzip` and `Vary: Accept-Encoding`

---

### High-Performance Transport Layer

The new `Transport/` subsystem mirrors Kestrel's architecture for maximum throughput under concurrent connections:

| Component | Role |
|---|---|
| `IOQueue` | Custom `PipeScheduler` that batches I/O continuations into a single `ThreadPool` work item per tick |
| `SocketSenderPool` | Pools `SocketSender` instances (wrapping `SocketAsyncEventArgs`) to eliminate per-send allocations |
| `SocketSender` / `SocketReceiver` | Async send/receive with pooled event args |
| `SocketTransportConnection` | Wires socket → `PipeReader` (input) and `PipeWriter` → socket (output) with independent read/write loops |
| `DuplexPipe` | `IDuplexPipe` implementation connecting transport and application pipe endpoints |

Connections are **round-robined** across `IOQueue.DefaultCount` queues (= `ProcessorCount / 2`, minimum 1) so that no single queue becomes a bottleneck.

---

## .NET 10 Support

The NuGet package now multi-targets `net8.0` and `net10.0`. On .NET 10:
- HTTP/3 / QUIC is available
- `Microsoft.Extensions.ObjectPool` 10.0.0 and `System.IO.Pipelines` 10.0.0 are used

No API changes are required when upgrading from .NET 8 to .NET 10.

---

## Bug Fixes (from v1.3.1)

These fixes are included in v2.0.0:

| # | Fix |
|---|---|
| 1 | HTTP/2 `SETTINGS` frame advertised `ENABLE_PUSH=1` — violates RFC 7540 §6.5.2 |
| 2 | Partial network reads in client preface / frame header could stall a connection |
| 3 | `PipeReader`/`PipeWriter` wrapper around `SslStream` interfered with HTTP/2 direct I/O |
| 4 | `HEADERS` + `DATA` frames written in separate calls; now batched in one `SslStream.WriteAsync` |
| 5 | HTTP/1.1: unknown method on a known route returned `501` instead of `405 Method Not Allowed` |
| 6 | `Expect: 100-continue` requests with a body were incorrectly rejected |

---

## Breaking Changes

None. v2.0.0 is fully backward-compatible with v1.x. All new features are opt-in via the fluent builder API.

---

## Roadmap Update

| Feature | Status |
|---|---|
| Response compression | ✅ DONE (v2.0.0) |
| WebSocket support | ✅ DONE (v2.0.0) |
| Static file serving | ✅ DONE (v2.0.0) |
| HTTP/3 / QUIC | ✅ DONE (v2.0.0, .NET 10+) |
| OpenAPI / Swagger | Planned |
| Rate limiting | Planned |
| Response caching | Planned |

---

## Migration Guide

### Upgrading from v1.x

No code changes required. Opt into new features via the builder:

```csharp
var app = EffinitiveApp.Create()
    .UsePort(5000)
    .UseHttpsPort(5001)
    .ConfigureTls(tls => { tls.CertificatePath = "cert.pfx"; tls.CertificatePassword = "pass"; })
    .UseResponseCompression()                  // NEW: gzip compression
    .UseStaticFiles("wwwroot")                 // NEW: static files
    .MapWebSocket("/ws", async (conn, ct) =>   // NEW: WebSocket
    {
        // ...
    })
    .MapEndpoints()
    .Build();
```

---

## Statistics

- **Version**: 2.0.0
- **Target frameworks**: net8.0, net10.0
- **New top-level features**: 4 (WebSocket, HTTP/3, Static Files, Compression)
- **New source files**: 12+
- **Breaking changes**: None
- **Backward compatible**: Yes
