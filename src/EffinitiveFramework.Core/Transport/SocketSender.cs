using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// Extension to get ArraySegment from ReadOnlyMemory for scatter/gather socket I/O.
/// </summary>
file static class MemoryExtensions
{
    public static ArraySegment<byte> GetArraySegment(this ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment))
            return segment;
        // Fallback: copy to a new array (should rarely happen with pool-backed memory)
        return new ArraySegment<byte>(memory.ToArray());
    }
}

/// <summary>
/// Wraps socket send operations with a poolable SocketAsyncEventArgs.
/// Supports scatter/gather I/O for multi-segment buffers — sends non-contiguous
/// PipeReader output in a single kernel call without copying.
/// </summary>
internal sealed class SocketSender : SocketAwaitableEventArgs
{
    private List<ArraySegment<byte>>? _bufferList;

    public SocketSender(PipeScheduler ioScheduler) : base(ioScheduler)
    {
    }

    /// <summary>
    /// Send all data in a ReadOnlySequence (potentially multi-segment from PipeReader).
    /// Uses scatter/gather I/O when the sequence has multiple segments.
    /// </summary>
    public ValueTask<SocketOperationResult> SendAsync(Socket socket, in ReadOnlySequence<byte> buffers)
    {
        if (buffers.IsSingleSegment)
        {
            return SendAsync(socket, buffers.First);
        }

        // Multi-segment: use BufferList for scatter/gather send
        SetBufferList(buffers);

        if (socket.SendAsync(this))
        {
            return new ValueTask<SocketOperationResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketOperationResult>(new SocketOperationResult(bytesTransferred))
            : new ValueTask<SocketOperationResult>(new SocketOperationResult(new SocketException((int)error)));
    }

    /// <summary>
    /// Clear buffers for return to pool. Small perf hit but prevents holding references
    /// to byte arrays while sitting in the pool.
    /// </summary>
    public void ClearForPool()
    {
        if (BufferList != null)
        {
            BufferList = null;
            _bufferList?.Clear();
        }
        else
        {
            SetBuffer(null, 0, 0);
        }
    }

    private ValueTask<SocketOperationResult> SendAsync(Socket socket, ReadOnlyMemory<byte> memory)
    {
        SetBuffer(MemoryMarshal.AsMemory(memory));

        if (socket.SendAsync(this))
        {
            return new ValueTask<SocketOperationResult>(this, 0);
        }

        var bytesTransferred = BytesTransferred;
        var error = SocketError;

        return error == SocketError.Success
            ? new ValueTask<SocketOperationResult>(new SocketOperationResult(bytesTransferred))
            : new ValueTask<SocketOperationResult>(new SocketOperationResult(new SocketException((int)error)));
    }

    private void SetBufferList(in ReadOnlySequence<byte> buffer)
    {
        _bufferList ??= new List<ArraySegment<byte>>();

        foreach (var segment in buffer)
        {
            _bufferList.Add(segment.GetArraySegment());
        }

        BufferList = _bufferList;
    }
}
