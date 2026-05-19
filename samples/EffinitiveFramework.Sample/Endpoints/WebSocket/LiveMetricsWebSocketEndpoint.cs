using System.Text;
using System.Text.Json;
using EffinitiveFramework.Core.WebSocket;

namespace EffinitiveFramework.Sample.Endpoints.WebSocket;

/// <summary>
/// WebSocket endpoint that pushes live server metrics to the client once per second.
/// Demonstrates server-to-client push over a persistent WebSocket connection.
///
/// Metrics emitted each tick:
///   - GC total memory (bytes)
///   - ThreadPool available worker / IO threads
///   - Process CPU time (total)
///   - Uptime in seconds
///
/// Register via: .MapWebSocket("/ws/metrics", (conn, ct) => new LiveMetricsWebSocketEndpoint().OnConnectedAsync(conn, ct))
/// Test with: wscat -c ws://localhost:5000/ws/metrics
/// </summary>
public class LiveMetricsWebSocketEndpoint : WebSocketEndpointBase
{
    private static readonly DateTime _processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public override string Route => "/ws/metrics";

    public override async Task OnConnectedAsync(
        WebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        // Announce the metric schema on connect
        var schema = new
        {
            @event = "schema",
            fields = new[] { "gcMemoryBytes", "workerThreadsFree", "ioThreadsFree", "uptimeSeconds", "timestamp" }
        };
        await connection.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(schema)),
            WebSocketMessageType.Text,
            cancellationToken);

        // Drain incoming frames (pings, close) without blocking the send loop.
        // RFC 6455: a server that only pushes must still process control frames.
        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                while (connection.IsOpen && !closeCts.IsCancellationRequested)
                {
                    var msg = await connection.ReceiveAsync(closeCts.Token);
                    if (msg == null) break; // Close frame or disconnect
                }
            }
            catch { }
            finally { closeCts.Cancel(); }
        }, closeCts.Token);

        // Push metrics every second; stop when the client closes or the server shuts down
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (connection.IsOpen && !closeCts.IsCancellationRequested)
        {
            try
            {
                await ticker.WaitForNextTickAsync(closeCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ThreadPool.GetAvailableThreads(out var workers, out var io);

            var payload = new
            {
                @event = "metrics",
                gcMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
                workerThreadsFree = workers,
                ioThreadsFree = io,
                uptimeSeconds = (long)(DateTime.UtcNow - _processStart).TotalSeconds,
                timestamp = DateTime.UtcNow.ToString("O")
            };

            try
            {
                await connection.SendAsync(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
                    WebSocketMessageType.Text,
                    closeCts.Token);
            }
            catch
            {
                break; // Client disconnected mid-send
            }
        }

        await receiveTask;
    }
}
