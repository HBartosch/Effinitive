using System.IO.Pipelines;
using System.Net.Sockets;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// Wraps socket receive operations with a reusable SocketAsyncEventArgs (per-connection).
/// Supports zero-byte receive (WaitForData) to avoid allocating buffers on idle connections.
/// </summary>
internal sealed class SocketReceiver : SocketAwaitableEventArgs
{
    public SocketReceiver(PipeScheduler ioScheduler) : base(ioScheduler)
    {
    }

    /// <summary>
    /// Issue a zero-byte receive to wait for data without allocating a buffer.
    /// The socket notifies when data is available, then we allocate and read.
    /// </summary>
    public ValueTask<SocketOperationResult> WaitForDataAsync(Socket socket)
    {
        SetBuffer(Memory<byte>.Empty);

        if (socket.ReceiveAsync(this))
        {
            // Async — OnCompleted will fire, continuation scheduled via IOQueue
            return new ValueTask<SocketOperationResult>(this, 0);
        }

        // Completed synchronously — return result directly, no IValueTaskSource involved
        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketOperationResult>(new SocketOperationResult(bytesTransferred))
            : new ValueTask<SocketOperationResult>(new SocketOperationResult(new SocketException((int)error)));
    }

    /// <summary>
    /// Receive data into the provided buffer.
    /// </summary>
    public ValueTask<SocketOperationResult> ReceiveAsync(Socket socket, Memory<byte> buffer)
    {
        SetBuffer(buffer);

        if (socket.ReceiveAsync(this))
        {
            return new ValueTask<SocketOperationResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketOperationResult>(new SocketOperationResult(bytesTransferred))
            : new ValueTask<SocketOperationResult>(new SocketOperationResult(new SocketException((int)error)));
    }
}
