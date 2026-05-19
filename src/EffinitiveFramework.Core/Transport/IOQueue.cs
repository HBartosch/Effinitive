using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// Custom PipeScheduler that batches I/O continuations into a single ThreadPool work item.
/// Instead of dispatching each continuation separately (default PipeScheduler.ThreadPool),
/// this queues work and drains the entire batch in one Execute() call — reducing ThreadPool
/// contention and context switches.
/// Modeled after Kestrel's IOQueue.
/// </summary>
internal sealed class IOQueue : PipeScheduler, IThreadPoolWorkItem
{
    /// <summary>
    /// Default number of IOQueues to create, based on processor count.
    /// Capped at 16 on Windows due to ThreadPool scheduling characteristics.
    /// </summary>
    public static readonly int DefaultCount = DetermineDefaultCount();

    private readonly ConcurrentQueue<Work> _workItems = new();
    private int _doingWork;

    public override void Schedule(Action<object?> action, object? state)
    {
        _workItems.Enqueue(new Work(action, state));

        // If we're not already processing, schedule ourselves on the ThreadPool.
        // CompareExchange ensures only one Execute() runs at a time.
        if (Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0)
        {
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        while (true)
        {
            // Drain all queued work
            while (_workItems.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }

            // Mark as not-working before checking for new items.
            // This ordering is critical — if reversed, we could miss items.
            _doingWork = 0;
            Thread.MemoryBarrier();

            if (_workItems.IsEmpty)
            {
                break;
            }

            // New work arrived between our last TryDequeue and setting _doingWork=0.
            // Re-acquire if nobody else has.
            if (Interlocked.Exchange(ref _doingWork, 1) == 1)
            {
                // Another thread already picked it up.
                break;
            }
        }
    }

    private static int DetermineDefaultCount()
    {
        int processorCount = Environment.ProcessorCount;
        if (OperatingSystem.IsWindows() || processorCount <= 32)
        {
            return Math.Min(processorCount, 16);
        }
        return processorCount / 2;
    }

    private readonly struct Work(Action<object?> callback, object? state)
    {
        public readonly Action<object?> Callback = callback;
        public readonly object? State = state;
    }
}
