using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks.Sources;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// A SocketAsyncEventArgs that implements IValueTaskSource for zero-allocation async socket ops.
/// Uses a direct _continuation field pattern (same as Kestrel) instead of ManualResetValueTaskSourceCore.
/// The token is always 0 — state resets naturally in GetResult by clearing _continuation.
/// The completion callback resumes via the IOQueue PipeScheduler.
/// </summary>
internal class SocketAwaitableEventArgs : SocketAsyncEventArgs, IValueTaskSource<SocketOperationResult>
{
    private static readonly Action<object?> _continuationCompleted = _ => { };

    private readonly PipeScheduler _ioScheduler;
    private Action<object?>? _continuation;

    public SocketAwaitableEventArgs(PipeScheduler ioScheduler)
        : base(unsafeSuppressExecutionContextFlow: true)
    {
        _ioScheduler = ioScheduler;
    }

    protected override void OnCompleted(SocketAsyncEventArgs _)
    {
        var c = _continuation;
        if (c != null ||
            (c = Interlocked.CompareExchange(ref _continuation, _continuationCompleted, null)) != null)
        {
            var continuationState = UserToken;
            UserToken = null;
            _continuation = _continuationCompleted;

            // Resume the awaiter on the IOQueue scheduler to batch continuations.
            _ioScheduler.Schedule(c, continuationState);
        }
    }

    public SocketOperationResult GetResult(short token)
    {
        // Clear continuation — this is the "reset" that makes the SAEA reusable
        _continuation = null;

        if (SocketError != SocketError.Success)
        {
            return new SocketOperationResult(new SocketException((int)SocketError));
        }

        return new SocketOperationResult(BytesTransferred);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        return ReferenceEquals(_continuation, _continuationCompleted)
            ? (SocketError == SocketError.Success
                ? ValueTaskSourceStatus.Succeeded
                : ValueTaskSourceStatus.Faulted)
            : ValueTaskSourceStatus.Pending;
    }

    public void OnCompleted(
        Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
    {
        UserToken = state;
        var prev = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (ReferenceEquals(prev, _continuationCompleted))
        {
            // Already completed before the awaiter registered — schedule immediately
            UserToken = null;
            _ioScheduler.Schedule(continuation, state);
        }
    }
}

/// <summary>
/// Result of a socket operation — either a byte count or an error.
/// </summary>
internal readonly struct SocketOperationResult(int bytesTransferred, SocketException? socketError)
{
    public readonly int BytesTransferred = bytesTransferred;
    public readonly SocketException? SocketError = socketError;
    public bool HasError => SocketError != null;

    public SocketOperationResult(int bytesTransferred) : this(bytesTransferred, null) { }
    public SocketOperationResult(SocketException error) : this(0, error) { }
}
