using System.Collections.Concurrent;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Represents an HTTP/2 stream
/// </summary>
public class Http2Stream
{
    public int StreamId { get; }
    public Http2StreamState State { get; private set; }
    public Dictionary<string, string> Headers { get; } = new();
    public MemoryStream DataBuffer { get; } = new();
    public TaskCompletionSource<bool> ResponseComplete { get; } = new();

    // Outbound (send) flow-control window. Starts at the peer's advertised
    // SETTINGS_INITIAL_WINDOW_SIZE and is consumed as DATA is sent, replenished by
    // WINDOW_UPDATE frames from the client. Guarded by _windowLock; never mutate directly.
    private readonly object _windowLock = new();
    private TaskCompletionSource<bool>? _windowWaiter;
    private int _windowSize;
    private bool _aborted;

    public Http2Stream(int streamId, int initialWindowSize)
    {
        StreamId = streamId;
        _windowSize = initialWindowSize;
        State = Http2StreamState.Idle;
    }

    /// <summary>Current send window. For diagnostics; the send path uses Acquire/Release.</summary>
    public int WindowSize { get { lock (_windowLock) return _windowSize; } }

    public void UpdateState(Http2StreamState newState)
    {
        State = newState;
    }

    /// <summary>
    /// Adjust the send window by <paramref name="delta"/> (positive for WINDOW_UPDATE,
    /// negative for SETTINGS_INITIAL_WINDOW_SIZE reductions). Wakes any sender waiting
    /// for window when the window becomes positive.
    /// </summary>
    public void UpdateWindowSize(int delta)
    {
        lock (_windowLock)
        {
            _windowSize += delta;
            if (_windowSize > 0)
                SignalNoLock();
        }
    }

    /// <summary>
    /// Reserve up to <paramref name="desired"/> bytes of send window, awaiting a
    /// WINDOW_UPDATE if the window is currently exhausted. Returns the number of bytes
    /// reserved (always &gt;= 1 on success). Throws if the stream is aborted/reset.
    /// </summary>
    public async Task<int> AcquireWindowAsync(int desired, CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource<bool> waiter;
            lock (_windowLock)
            {
                if (_aborted)
                    throw new OperationCanceledException("Stream reset while awaiting flow-control window");
                if (_windowSize > 0)
                {
                    int grant = Math.Min(desired, _windowSize);
                    _windowSize -= grant;
                    return grant;
                }
                waiter = _windowWaiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await waiter.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Try to reserve exactly <paramref name="n"/> bytes without awaiting.</summary>
    public bool TryReserveWindow(int n)
    {
        lock (_windowLock)
        {
            if (_aborted || _windowSize < n)
                return false;
            _windowSize -= n;
            return true;
        }
    }

    /// <summary>Return previously reserved-but-unused window.</summary>
    public void ReleaseWindow(int amount)
    {
        if (amount <= 0) return;
        UpdateWindowSize(amount);
    }

    /// <summary>Unblock any sender awaiting window (e.g. on RST_STREAM); they will throw.</summary>
    public void Abort()
    {
        lock (_windowLock)
        {
            _aborted = true;
            SignalNoLock();
        }
    }

    private void SignalNoLock()
    {
        var w = _windowWaiter;
        _windowWaiter = null;
        w?.TrySetResult(true);
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
