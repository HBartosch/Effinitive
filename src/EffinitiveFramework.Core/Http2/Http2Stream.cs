using System.Collections.Concurrent;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Represents an HTTP/2 stream
/// </summary>
public class Http2Stream
{
    public int StreamId { get; }
    public Http2StreamState State { get; private set; }
    public int WindowSize { get; private set; }
    public Dictionary<string, string> Headers { get; } = new();
    public MemoryStream DataBuffer { get; } = new();
    public TaskCompletionSource<bool> ResponseComplete { get; } = new();
    
    public Http2Stream(int streamId, int initialWindowSize)
    {
        StreamId = streamId;
        WindowSize = initialWindowSize;
        State = Http2StreamState.Idle;
    }
    
    public void UpdateState(Http2StreamState newState)
    {
        State = newState;
    }
    
    public void UpdateWindowSize(int delta)
    {
        WindowSize += delta;
    }
    
    public void AddHeader(string name, string value)
    {
        Headers[name.ToLowerInvariant()] = value;
    }
    
    public void AppendData(ReadOnlySpan<byte> data)
    {
        DataBuffer.Write(data);
    }
}

/// <summary>
/// HTTP/2 stream states
/// </summary>
public enum Http2StreamState
{
    Idle,
    ReservedLocal,
    ReservedRemote,
    Open,
    HalfClosedLocal,
    HalfClosedRemote,
    Closed
}
