# Server-Sent Events (SSE) in EffinitiveFramework

EffinitiveFramework includes built-in support for Server-Sent Events (SSE), allowing you to push real-time updates from the server to clients over HTTP.

## What is SSE?

Server-Sent Events is a standard for pushing data from server to client over HTTP. Unlike WebSockets (which are bidirectional), SSE is unidirectional (server → client) and works over regular HTTP/HTTPS, making it simpler and more lightweight for many use cases.

**Use cases:**
- Real-time dashboards
- Live notifications
- Progress indicators
- Stock tickers
- Chat applications (server → client only)
- IoT sensor data streaming

## Creating an SSE Endpoint

### Simple SSE Endpoint

```csharp
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Http.ServerSentEvents;

public class ServerTimeStreamEndpoint : SseEndpointBase<EmptyRequest>
{
    protected override string Method => "GET";
    protected override string Route => "/api/stream/time";

    protected override async Task HandleStreamAsync(
        EmptyRequest request, 
        SseStream stream, 
        CancellationToken cancellationToken)
    {
        // Start keep-alive ping every 15 seconds
        var keepAliveTask = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(15), cancellationToken);

        try
        {
            // Send initial connection event
            await stream.WriteAsync("connected", "Stream started", cancellationToken);

            // Stream time updates every second
            while (!cancellationToken.IsCancellationRequested)
            {
                var timeData = new
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    datetime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                };

                await stream.WriteJsonAsync("time-update", timeData, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }
}
```

### SSE Endpoint with Request Parameters

```csharp
public class StockPriceRequest
{
    public string Symbol { get; set; } = string.Empty;
}

public class StockPriceStreamEndpoint : SseEndpointBase<StockPriceRequest>
{
    protected override string Method => "POST";
    protected override string Route => "/api/stream/stock";

    protected override async Task HandleStreamAsync(
        StockPriceRequest request, 
        SseStream stream, 
        CancellationToken cancellationToken)
    {
        var keepAliveTask = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(30), cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Simulate fetching stock price
                var price = GetStockPrice(request.Symbol);
                
                await stream.WriteJsonAsync("price-update", new
                {
                    symbol = request.Symbol,
                    price = price,
                    timestamp = DateTime.UtcNow
                }, cancellationToken);

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await stream.WriteAsync("disconnected", $"Stopped streaming {request.Symbol}", CancellationToken.None);
        }
    }

    private decimal GetStockPrice(string symbol)
    {
        // Your stock price logic here
        return Random.Shared.Next(100, 200) + (decimal)Random.Shared.NextDouble();
    }
}
```

## SseStream API

The `SseStream` class provides several methods for sending data:

### Basic Methods

```csharp
// Write simple data message
await stream.WriteAsync("Hello, client!");

// Write typed event with data
await stream.WriteAsync("custom-event", "Event data");

// Write custom SSE event
var evt = new SseEvent 
{ 
    Id = "123",
    Event = "update",
    Data = "Some data",
    Retry = 5000
};
await stream.WriteEventAsync(evt);
```

### JSON Methods

```csharp
// Write JSON data as default "message" event
await stream.WriteJsonAsync(new { name = "John", age = 30 });

// Write JSON data as typed event
await stream.WriteJsonAsync("user-update", new { name = "John", age = 30 });
```

### Keep-Alive

```csharp
// Manual keep-alive ping
await stream.WriteKeepAliveAsync();

// Automatic keep-alive every 15 seconds
var keepAliveTask = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(15), cancellationToken);
```

## Client-Side Consumption

### JavaScript/Browser

```javascript
const eventSource = new EventSource('/api/stream/time');

// Listen for default "message" events
eventSource.onmessage = (event) => {
    const data = JSON.parse(event.data);
    console.log('Received:', data);
};

// Listen for custom event types
eventSource.addEventListener('time-update', (event) => {
    const data = JSON.parse(event.data);
    console.log('Time:', data.datetime);
});

// Handle errors
eventSource.onerror = (error) => {
    console.error('SSE error:', error);
    eventSource.close();
};

// Close connection when done
eventSource.close();
```

### C# Client

```csharp
using var client = new HttpClient();
using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/api/stream/time");

using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
using var stream = await response.Content.ReadAsStreamAsync();
using var reader = new StreamReader(stream);

while (!cancellationToken.IsCancellationRequested)
{
    var line = await reader.ReadLineAsync(cancellationToken);
    if (line == null) break;
    
    if (line.StartsWith("data: "))
    {
        var data = line.Substring(6);
        Console.WriteLine($"Received: {data}");
    }
}
```

## SSE Event Format

SSE events follow this format (per [W3C EventSource spec](https://html.spec.whatwg.org/multipage/server-sent-events.html)):

```
id: 123
event: custom-event
data: First line
data: Second line
retry: 5000

```

- `id:` - Optional event ID for reconnection
- `event:` - Optional event type (defaults to "message")
- `data:` - Event payload (can be multi-line)
- `retry:` - Optional reconnection interval in milliseconds
- Ends with blank line (`\n\n`)

## Response Headers

SSE endpoints automatically set the following headers:

```
HTTP/1.1 200 OK
Content-Type: text/event-stream
Cache-Control: no-cache
Connection: keep-alive
X-Accel-Buffering: no
```

## Best Practices

### 1. Keep-Alive Pings

Always send periodic keep-alive comments to prevent connection timeouts:

```csharp
var keepAliveTask = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(15), cancellationToken);
```

### 2. Handle Cancellation

Properly handle client disconnections:

```csharp
try
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await stream.WriteJsonAsync(data, cancellationToken);
        await Task.Delay(interval, cancellationToken);
    }
}
catch (OperationCanceledException)
{
    // Client disconnected - cleanup
}
```

### 3. Resource Cleanup

Use `await using` for automatic disposal:

```csharp
await using var sseStream = new SseStream(stream);
```

### 4. Event IDs for Reconnection

Include event IDs to support client reconnection:

```csharp
var evt = new SseEvent 
{ 
    Id = Guid.NewGuid().ToString(),
    Data = "Event data"
};
await stream.WriteEventAsync(evt);
```

### 5. Error Handling

Send error events instead of throwing exceptions:

```csharp
try
{
    var data = await FetchDataAsync();
    await stream.WriteJsonAsync(data);
}
catch (Exception ex)
{
    await stream.WriteAsync("error", ex.Message);
}
```

## Performance Considerations

- **Memory**: Each SSE connection holds an open socket. Monitor concurrent connections.
- **Buffering**: SSE automatically disables buffering (`X-Accel-Buffering: no`)
- **Keep-Alive**: Use 15-30 second intervals to prevent proxy/firewall timeouts
- **Backpressure**: Consider rate-limiting if client can't keep up

## SSE vs WebSockets

| Feature | SSE | WebSockets |
|---------|-----|------------|
| Direction | Server → Client | Bidirectional |
| Protocol | HTTP/HTTPS | ws:// / wss:// |
| Browser Support | Excellent (all modern) | Excellent |
| Reconnection | Automatic | Manual |
| Complexity | Simple | More complex |
| Overhead | Lower | Higher |
| Use Case | Real-time updates | Two-way communication |

Choose SSE when:
- You only need server → client communication
- You want automatic reconnection
- You want to work over standard HTTP/HTTPS
- You need simpler implementation

Choose WebSockets when:
- You need bidirectional communication
- You need lower latency
- You're building real-time collaboration tools

## Example: Progress Indicator

```csharp
public class ProgressStreamEndpoint : SseEndpointBase<EmptyRequest>
{
    protected override string Method => "GET";
    protected override string Route => "/api/stream/progress";

    protected override async Task HandleStreamAsync(
        EmptyRequest request, 
        SseStream stream, 
        CancellationToken cancellationToken)
    {
        for (int i = 0; i <= 100; i += 10)
        {
            await stream.WriteJsonAsync("progress", new
            {
                percentage = i,
                status = i == 100 ? "Complete" : "Processing..."
            }, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await stream.WriteAsync("done", "Process completed");
    }
}
```

## Troubleshooting

### Connection Drops Immediately

- Check firewall/proxy settings
- Verify Content-Type is `text/event-stream`
- Add keep-alive pings

### Events Not Arriving

- Ensure events end with `\n\n`
- Check buffering is disabled
- Verify stream is being flushed

### Memory Leaks

- Always use `CancellationToken`
- Properly dispose `SseStream`
- Limit concurrent connections

## See Also

- [MDN: Server-Sent Events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events)
- [W3C EventSource Specification](https://html.spec.whatwg.org/multipage/server-sent-events.html)
- [RFC 7231 §4.2.2 (GET)](https://tools.ietf.org/html/rfc7231#section-4.2.2)
