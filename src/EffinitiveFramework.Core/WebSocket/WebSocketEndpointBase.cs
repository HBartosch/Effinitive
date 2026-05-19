namespace EffinitiveFramework.Core.WebSocket;

/// <summary>
/// Base class for WebSocket endpoint handlers.
/// Subclass this and override OnConnectedAsync to handle WebSocket connections.
/// </summary>
public abstract class WebSocketEndpointBase
{
    /// <summary>
    /// The route this WebSocket endpoint handles (e.g., "/ws").
    /// </summary>
    public abstract string Route { get; }

    /// <summary>
    /// Called when a WebSocket connection is established.
    /// Override to implement echo, chat, or other WebSocket logic.
    /// The connection is automatically closed when this method returns.
    /// </summary>
    public abstract Task OnConnectedAsync(WebSocketConnection connection, CancellationToken cancellationToken);
}
