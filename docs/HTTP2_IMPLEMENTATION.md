# HTTP/2 Implementation Guide

## Overview

EffinitiveFramework includes a complete HTTP/2 implementation built from the ground up for maximum performance. The implementation follows RFC 7540 (HTTP/2) and RFC 7541 (HPACK).

## Architecture

### Components

1. **Http2Connection** - Manages the lifecycle of an HTTP/2 connection
   - Connection preface handling
   - Settings negotiation
   - Frame processing
   - Stream management
   - Flow control

2. **Http2Frame** - Binary frame parser and serializer
   - 9 frame types: DATA, HEADERS, PRIORITY, RST_STREAM, SETTINGS, PUSH_PROMISE, PING, GOAWAY, WINDOW_UPDATE
   - Frame header encoding/decoding (24-bit length, 8-bit type, 8-bit flags, 31-bit stream ID)
   - Zero-copy payload handling

3. **Http2Stream** - Individual request/response stream
   - Stream state machine (idle → open → half-closed → closed)
   - Per-stream flow control
   - Header accumulation
   - Data buffering

4. **HPACK Implementation**
   - **HpackEncoder** - Compress headers using static table, dynamic table, and Huffman coding
   - **HpackDecoder** - Decompress headers
   - **HpackStaticTable** - 61 pre-defined header entries (RFC 7541 Appendix A)
   - **HpackDynamicTable** - LRU cache with eviction based on size limits
   - **HuffmanDecoder** - Huffman decoding (placeholder for full implementation)

## Protocol Negotiation

HTTP/2 is negotiated via ALPN (Application-Layer Protocol Negotiation) during the TLS handshake:

```csharp
var sslOptions = new SslServerAuthenticationOptions
{
    ServerCertificate = certificate,
    ApplicationProtocols = new List<SslApplicationProtocol>
    {
        SslApplicationProtocol.Http2,    // "h2"
        SslApplicationProtocol.Http11     // "http/1.1"
    }
};
```

When a client supports HTTP/2, it will include "h2" in its ALPN extension. The server detects this and routes the connection to the HTTP/2 handler.

## Connection Flow

### 1. Connection Establishment

```
Client                                  Server
  |                                       |
  |-------- TCP Connection ------------->|
  |<------- TLS Handshake (ALPN) --------|
  |-------- HTTP/2 Preface ------------->|
  |        PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n
  |<------- SETTINGS Frame --------------|
  |-------- SETTINGS Frame ------------->|
  |<------- SETTINGS ACK -----------------|
  |-------- SETTINGS ACK ---------------->|
```

### 2. Request/Response

```
Client                                  Server
  |                                       |
  |-------- HEADERS Frame --------------->|
  |        (stream 1, END_HEADERS)        |
  |                                       |
  |<------- HEADERS Frame ----------------|
  |        (stream 1, END_HEADERS)        |
  |<------- DATA Frame -------------------|
  |        (stream 1, END_STREAM)         |
```

### 3. Multiplexing

Multiple streams can be active simultaneously:

```
Stream 1: HEADERS → (processing) → HEADERS → DATA
Stream 3: HEADERS → DATA → (processing) → HEADERS → DATA
Stream 5: HEADERS → (processing) → HEADERS → DATA
```

## Frame Format

All HTTP/2 frames follow this structure:

```
+-----------------------------------------------+
|                 Length (24)                   |
+---------------+---------------+---------------+
|   Type (8)    |   Flags (8)   |
+-+-------------+---------------+-------------------------------+
|R|                 Stream Identifier (31)                      |
+=+=============================================================+
|                   Frame Payload (0...)                      ...
+---------------------------------------------------------------+
```

## HPACK Compression

Headers are compressed using HPACK to reduce overhead:

### Static Table Example
```
Index | Name          | Value
------|---------------|--------
1     | :authority    |
2     | :method       | GET
3     | :method       | POST
4     | :path         | /
5     | :path         | /index.html
6     | :scheme       | http
7     | :scheme       | https
8     | :status       | 200
...
```

### Encoding Strategies

1. **Indexed Header Field** - Reference existing table entry (1 byte for common headers)
2. **Literal with Incremental Indexing** - New header added to dynamic table
3. **Literal without Indexing** - One-time header not cached
4. **Literal Never Indexed** - Sensitive data (e.g., cookies)

### Example

```csharp
// Request headers
var headers = new List<(string, string)>
{
    (":method", "GET"),       // Indexed: 0x82 (1 byte)
    (":path", "/api/users"),  // Literal: 0x44 + encoded string
    (":scheme", "https"),     // Indexed: 0x87 (1 byte)
    ("accept", "application/json")  // Literal with indexing
};

// Compressed size: ~20 bytes instead of ~60 bytes
```

## Performance Optimizations

### 1. Zero-Copy Frame Handling
```csharp
// Frame payload is sliced from read buffer, no copying
frame.Payload = buffer.AsMemory(offset, frame.Length);
```

### 2. ArrayPool for Buffers
```csharp
var payloadBuffer = ArrayPool<byte>.Shared.Rent(frame.Length);
try
{
    // Use buffer
}
finally
{
    ArrayPool<byte>.Shared.Return(payloadBuffer);
}
```

### 3. Span<T> for Parsing
```csharp
public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out Http2Frame frame)
{
    // Parse 9-byte header without allocation
    frame.Length = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
    frame.Type = buffer[3];
    frame.Flags = buffer[4];
    frame.StreamId = BinaryPrimitives.ReadInt32BigEndian(buffer[5..9]) & 0x7FFFFFFF;
    return true;
}
```

### 4. Static Table Lookup
```csharp
// O(1) lookup for common headers
if (index <= 61)
    return HpackStaticTable.Entries[index - 1];
```

## Flow Control

HTTP/2 implements flow control at two levels:

### Connection-Level
- Initial window: 65,535 bytes
- Applies to all streams
- Updated via WINDOW_UPDATE frames on stream 0

## Server Push

EffinitiveFramework supports HTTP/2 server push, allowing servers to proactively send resources to clients before they are requested. This is ideal for scenarios like:

- **Hot Reload / Live Development** - Push updated compiled modules to the browser
- **Optimizing Page Load** - Push CSS, JS, images referenced by HTML
- **API Preloading** - Push API data the client will likely need

### How Server Push Works

1. Client requests resource (e.g., `/index.html`) on stream 1
2. Server sends PUSH_PROMISE frame on stream 1, announcing it will push `/app.css` on stream 2
3. Server sends HEADERS + DATA frames on stream 2 with the CSS file
4. Client receives the pushed resource before requesting it

### Using Server Push API

```csharp
// Access the HTTP/2 connection from your endpoint
server.MapGet("/", async (HttpRequest request, Http2Connection? http2Connection) =>
{
    if (http2Connection != null)
    {
        // Push CSS file before client requests it
        await http2Connection.PushResourceAsync(
            associatedStreamId: 1, // The stream that triggered this push
            requestHeaders: new Dictionary<string, string>
            {
                { ":method", "GET" },
                { ":path", "/styles/app.css" },
                { ":scheme", "https" },
                { ":authority", "localhost:5001" }
            },
            responseHeaders: new Dictionary<string, string>
            {
                { ":status", "200" },
                { "content-type", "text/css" },
                { "content-length", cssBytes.Length.ToString() }
            },
            responseBody: cssBytes,
            cancellationToken: default
        );
    }
    
    return new HttpResponse { /* ... */ };
});
```

### Hot Reload Use Case

Server push is perfect for hot-reload scenarios where the server detects code changes and pushes updated modules to the browser:

```csharp
// Track active HTTP/2 connections
var http2Connections = new ConcurrentDictionary<Http2Connection, byte>();

// File watcher for module changes
var watcher = new FileSystemWatcher("./modules", "*.cs");
watcher.Changed += async (sender, e) =>
{
    // Compile the module
    var compiledModule = await CompileModuleAsync(e.FullPath);
    
    // Push to all connected clients
    foreach (var (connection, _) in http2Connections)
    {
        await connection.PushResourceAsync(
            associatedStreamId: 1,
            requestHeaders: new Dictionary<string, string>
            {
                { ":method", "GET" },
                { ":path", $"/modules/{e.Name}.dll" },
                { ":scheme", "https" },
                { ":authority", "localhost:5001" }
            },
            responseHeaders: new Dictionary<string, string>
            {
                { ":status", "200" },
                { "content-type", "application/octet-stream" },
                { "x-hot-reload", "true" }
            },
            responseBody: compiledModule
        );
    }
};
```

On the client side (browser), you can detect pushed resources:

```javascript
// Detect HTTP/2 pushed resources
const observer = new PerformanceObserver((list) => {
    list.getEntries().forEach((entry) => {
        if (entry.entryType === 'resource' && entry.name.includes('/modules/')) {
            console.log('Received pushed module:', entry.name);
            hotReloadModule(entry.name);
        }
    });
});
observer.observe({ entryTypes: ['resource'] });
```

### Server Push Settings

Server push is enabled by default (`ENABLE_PUSH = 1`). Clients can disable it by sending `SETTINGS_ENABLE_PUSH = 0`:

```csharp
// Check if push is enabled before pushing
if (_enablePush == 0)
    throw new InvalidOperationException("Server push is disabled by client");
```

### Stream ID Allocation

- Client-initiated streams use **odd** stream IDs (1, 3, 5, ...)
- Server-initiated (pushed) streams use **even** stream IDs (2, 4, 6, ...)

The framework automatically manages pushed stream IDs starting from 2 and incrementing by 2.

### Performance Considerations

- **Only push resources the client will need** - Avoid pushing unnecessary data
- **Respect flow control** - Don't exceed client's window size
- **Cache pushed resources** - Use browser cache headers to avoid re-pushing
- **Monitor client SETTINGS** - Respect `ENABLE_PUSH = 0` if client disables push

### Full Example

See `samples/EffinitiveFramework.HotReload.Sample` for a complete working example of using server push for hot-reloading C# modules in a Blazor-like scenario.

### Stream-Level
- Initial window: 65,535 bytes (configurable via SETTINGS)
- Independent per stream
- Updated via WINDOW_UPDATE frames

```csharp
// Send data respecting flow control
while (dataRemaining > 0 && stream.WindowSize > 0)
{
    var chunkSize = Math.Min(dataRemaining, stream.WindowSize);
    await SendDataFrameAsync(stream.StreamId, data, chunkSize);
    stream.UpdateWindowSize(-chunkSize);
    dataRemaining -= chunkSize;
}
```

## Error Handling

### Connection Errors
Send GOAWAY frame with error code:
```csharp
await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
```

### Stream Errors
Send RST_STREAM frame:
```csharp
await SendRstStreamAsync(streamId, Http2Constants.ErrorCancel, cancellationToken);
```

## Current Status

✅ **Implemented**
- Binary frame parser/serializer
- SETTINGS frame exchange
- PING/PONG
- GOAWAY
- ALPN negotiation
- Basic HPACK encoding/decoding
- Static table lookup
- Dynamic table management

⏳ **In Progress**
- Complete HEADERS frame processing
- DATA frame handling with flow control
- Full Huffman encoding/decoding
- Stream multiplexing
- Priority and dependencies
- Complete request/response cycle

🔜 **Planned**
- Server push (PUSH_PROMISE)
- Complete flow control implementation
- Performance benchmarks vs HTTP/1.1
- Integration tests with real HTTP/2 clients

## Testing

Test HTTP/2 support with curl:
```bash
# Test HTTP/2 (requires HTTPS)
curl -k --http2 https://localhost:5001/api/benchmark -v

# You should see:
# * ALPN, offering h2
# * ALPN, server accepted to use h2
```

Test with Chrome DevTools:
1. Open Chrome DevTools (F12)
2. Navigate to Network tab
3. Look for "Protocol" column showing "h2"

## References

- [RFC 7540 - HTTP/2](https://tools.ietf.org/html/rfc7540)
- [RFC 7541 - HPACK](https://tools.ietf.org/html/rfc7541)
- [ALPN Extension](https://tools.ietf.org/html/rfc7301)
- [HTTP/2 Specification](https://http2.github.io/)
