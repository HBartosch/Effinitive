using System.Text;
using EffinitiveFramework.Core.WebSocket;

namespace EffinitiveFramework.Sample.Endpoints.WebSocket;

/// <summary>
/// Class-based WebSocket echo server.
/// Demonstrates WebSocketEndpointBase: every message the client sends is echoed back with
/// a timestamp prefix. The server also sends a welcome message on connect.
///
/// Register via: .MapWebSocket("/ws/echo", (conn, ct) => new EchoWebSocketEndpoint().OnConnectedAsync(conn, ct))
/// Test with: wscat -c ws://localhost:5000/ws/echo
/// </summary>
public class EchoWebSocketEndpoint : WebSocketEndpointBase
{
    public override string Route => "/ws/echo";

    public override async Task OnConnectedAsync(
        WebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        // Send a welcome banner on connect
        await connection.SendAsync(
            Encoding.UTF8.GetBytes("""{"event":"connected","message":"Echo server ready. Send any message to have it echoed back."}"""),
            WebSocketMessageType.Text,
            cancellationToken);

        while (connection.IsOpen && !cancellationToken.IsCancellationRequested)
        {
            var message = await connection.ReceiveAsync(cancellationToken);

            if (message == null)
                break; // Client closed the connection

            if (message.Value.Type == WebSocketMessageType.Text)
            {
                var original = message.Value.GetText();
                var serializedOriginal = System.Text.Json.JsonSerializer.Serialize(original);
                var echoed = $"{{\"event\":\"echo\",\"original\":{serializedOriginal},\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
                await connection.SendAsync(
                    Encoding.UTF8.GetBytes(echoed),
                    WebSocketMessageType.Text,
                    cancellationToken);
            }
            else
            {
                // Binary: echo back unchanged
                await connection.SendAsync(
                    message.Value.Data,
                    WebSocketMessageType.Binary,
                    cancellationToken);
            }
        }
    }
}
