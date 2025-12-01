using System.Text;

namespace EffinitiveFramework.Core.Http.ServerSentEvents;

/// <summary>
/// Represents a Server-Sent Event (SSE) message
/// </summary>
public sealed class SseEvent
{
    /// <summary>
    /// Event type (optional, defaults to "message")
    /// </summary>
    public string? Event { get; set; }

    /// <summary>
    /// Event data (required)
    /// </summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// Event ID (optional, for reconnection)
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Retry interval in milliseconds (optional)
    /// </summary>
    public int? Retry { get; set; }

    /// <summary>
    /// Format the event according to SSE specification
    /// </summary>
    public byte[] ToBytes()
    {
        var sb = new StringBuilder();

        // Event ID
        if (!string.IsNullOrEmpty(Id))
        {
            sb.Append("id: ").Append(Id).Append("\n");
        }

        // Event type
        if (!string.IsNullOrEmpty(Event))
        {
            sb.Append("event: ").Append(Event).Append("\n");
        }

        // Retry interval
        if (Retry.HasValue)
        {
            sb.Append("retry: ").Append(Retry.Value).Append("\n");
        }

        // Data (can be multi-line)
        if (!string.IsNullOrEmpty(Data))
        {
            var lines = Data.Split('\n');
            foreach (var line in lines)
            {
                sb.Append("data: ").Append(line).Append("\n");
            }
        }

        // End with blank line
        sb.Append("\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Create a simple data-only event
    /// </summary>
    public static SseEvent Message(string data) => new() { Data = data };

    /// <summary>
    /// Create a typed event with data
    /// </summary>
    public static SseEvent Typed(string eventType, string data) => new() { Event = eventType, Data = data };

    /// <summary>
    /// Create a comment (ignored by client, used for keep-alive)
    /// </summary>
    public static byte[] Comment(string text = "")
    {
        return Encoding.UTF8.GetBytes($": {text}\n\n");
    }
}
