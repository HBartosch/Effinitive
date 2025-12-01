using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Http.ServerSentEvents;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example SSE endpoint that streams real-time events
/// </summary>
public class ServerTimeStreamEndpoint : NoRequestSseEndpointBase
{
    protected override string Method => "GET";
    protected override string Route => "/api/stream/time";

    protected override async Task HandleStreamAsync(
        SseStream stream, 
        CancellationToken cancellationToken)
    {
        // Start keep-alive ping every 15 seconds
        var keepAliveTask = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(15), cancellationToken);

        try
        {
            // Send initial event
            await stream.WriteAsync("connected", "Stream started", cancellationToken);

            // Stream time updates every second
            while (!cancellationToken.IsCancellationRequested)
            {
                var timeData = new
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    datetime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    message = "Server time update"
                };

                await stream.WriteJsonAsync("time-update", timeData, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - cleanup
            await stream.WriteAsync("disconnected", "Stream closed", CancellationToken.None);
        }
        finally
        {
            await keepAliveTask;
        }
    }
}
