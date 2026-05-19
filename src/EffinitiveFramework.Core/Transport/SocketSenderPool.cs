using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// Thread-safe pool for SocketSender objects backed by ConcurrentQueue.
/// SocketSenders wrap SocketAsyncEventArgs and are expensive to create,
/// but cheap to reuse. Rented per-send, returned after completion.
/// </summary>
internal sealed class SocketSenderPool : IDisposable
{
    private const int MaxPoolSize = 1024;

    private readonly ConcurrentQueue<SocketSender> _queue = new();
    private readonly PipeScheduler _scheduler;
    private int _count;
    private bool _disposed;

    public SocketSenderPool(PipeScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public PipeScheduler Scheduler => _scheduler;

    public SocketSender Rent()
    {
        if (_queue.TryDequeue(out var sender))
        {
            Interlocked.Decrement(ref _count);
            return sender;
        }

        return new SocketSender(_scheduler);
    }

    public void Return(SocketSender sender)
    {
        if (_disposed || Interlocked.Increment(ref _count) > MaxPoolSize)
        {
            Interlocked.Decrement(ref _count);
            sender.Dispose();
            return;
        }

        sender.ClearForPool();
        _queue.Enqueue(sender);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_queue.TryDequeue(out var sender))
        {
            sender.Dispose();
        }
    }
}
