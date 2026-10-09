using System.Collections.Concurrent;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Represents an HTTP/2 stream
/// </summary>
public class Http2Stream
{
    public int StreamId { get; }
    public Http2StreamState State { get; private set; }

    // Sized for the pseudo-headers plus a handful of real ones, so a typical
    // request fills it without the dictionary resizing on the way.
    public Dictionary<string, string> Headers { get; } = new(8);

    private MemoryStream? _dataBuffer;

    /// <summary>
    /// Whether any DATA frame has arrived on this stream.
    /// </summary>
    /// <remarks>
    /// A server can hold a great many concurrent streams at once, and most
    /// carry no request body at all, so the buffer is created on the first
    /// write rather than with the stream. Check this before touching
    /// <see cref="DataBuffer"/>, which allocates on access to keep the property
    /// non-null for callers that expect a stream to be there.
    /// </remarks>
    public bool HasBody => _dataBuffer is { Length: > 0 };

    public MemoryStream DataBuffer => _dataBuffer ??= new MemoryStream();

    // Outbound (send) flow-control window. Starts at the peer's advertised
    // SETTINGS_INITIAL_WINDOW_SIZE and is consumed as DATA is sent, replenished by
    // WINDOW_UPDATE frames from the client.
    //
    // _windowSize and _aborted are read and written with interlocked and volatile operations,
    // not under _windowLock, because every response on the connection reserves from them and a
    // Monitor there serialises all of a connection's streams. _windowLock now guards only the
    // waiter field, which is touched when a sender has to park and when one is woken.
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
    public int WindowSize => Volatile.Read(ref _windowSize);

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
        if (Interlocked.Add(ref _windowSize, delta) > 0)
            SignalWaiter();
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
            ThrowIfAborted();

            int grant = TakeUpToAvailable(desired);
            if (grant > 0)
                return grant;

            TaskCompletionSource<bool> waiter;
            lock (_windowLock)
            {
                // Re-checked under the lock so a grant or an abort that lands between the
                // attempt above and the registration below is not slept through.
                ThrowIfAborted();

                grant = TakeUpToAvailable(desired);
                if (grant > 0)
                    return grant;

                waiter = _windowWaiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await waiter.Task.WaitAsync(cancellationToken);
        }
    }

    private void ThrowIfAborted()
    {
        if (Volatile.Read(ref _aborted))
            throw new OperationCanceledException("Stream reset while awaiting flow-control window");
    }

    /// <summary>Take as much of <paramref name="desired"/> as the window holds, or nothing.</summary>
    private int TakeUpToAvailable(int desired)
    {
        while (true)
        {
            int current = Volatile.Read(ref _windowSize);
            if (current <= 0)
                return 0;

            int grant = Math.Min(desired, current);
            if (Interlocked.CompareExchange(ref _windowSize, current - grant, current) == current)
                return grant;
        }
    }

    /// <summary>Try to reserve exactly <paramref name="n"/> bytes without awaiting.</summary>
    public bool TryReserveWindow(int n)
    {
        // Lock-free for the same reason as the connection window: this is on the path of every
        // response, and a Monitor here serialises all of a connection's streams. A window that
        // has gone negative through a SETTINGS reduction (RFC 9113 §6.9.2) fails the test
        // below like any other short window, so nothing is sent until it recovers.
        while (true)
        {
            if (Volatile.Read(ref _aborted))
                return false;

            int current = Volatile.Read(ref _windowSize);
            if (current < n)
                return false;

            if (Interlocked.CompareExchange(ref _windowSize, current - n, current) == current)
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
        Volatile.Write(ref _aborted, true);
        SignalWaiter();
    }

    /// <summary>Wake a sender parked on this stream's window, if there is one.</summary>
    private void SignalWaiter()
    {
        if (Volatile.Read(ref _windowWaiter) is null)
            return;

        TaskCompletionSource<bool>? waiter;
        lock (_windowLock)
        {
            waiter = _windowWaiter;
            _windowWaiter = null;
        }

        waiter?.TrySetResult(true);
    }

    public void AddHeader(string name, string value)
    {
        Headers[name.ToLowerInvariant()] = value;
    }

    public void AppendData(ReadOnlySpan<byte> data)
    {
        (_dataBuffer ??= new MemoryStream()).Write(data);
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
