using System.Text;
using System.Text.Json;

namespace EffinitiveFramework.Core.Http.ServerSentEvents;

/// <summary>
/// Server-Sent Events (SSE) stream writer
/// </summary>
public sealed class SseStream : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public SseStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Write an SSE event to the stream
    /// </summary>
    public async Task WriteEventAsync(SseEvent sseEvent, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        
        await _writeLock.WaitAsync(linkedCts.Token);
        try
        {
            var bytes = sseEvent.ToBytes();
            await _stream.WriteAsync(bytes, linkedCts.Token);
            await _stream.FlushAsync(linkedCts.Token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Write a simple data message
    /// </summary>
    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        return WriteEventAsync(SseEvent.Message(data), cancellationToken);
    }

    /// <summary>
    /// Write a typed event with data
    /// </summary>
    public Task WriteAsync(string eventType, string data, CancellationToken cancellationToken = default)
    {
        return WriteEventAsync(SseEvent.Typed(eventType, data), cancellationToken);
    }

    /// <summary>
    /// Write JSON data as an event
    /// </summary>
    public Task WriteJsonAsync<T>(T data, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(data);
        return WriteAsync(json, cancellationToken);
    }

    /// <summary>
    /// Write JSON data as a typed event
    /// </summary>
    public Task WriteJsonAsync<T>(string eventType, T data, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(data);
        return WriteAsync(eventType, json, cancellationToken);
    }

    /// <summary>
    /// Write a keep-alive comment (prevents timeout)
    /// </summary>
    public async Task WriteKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        
        await _writeLock.WaitAsync(linkedCts.Token);
        try
        {
            var bytes = SseEvent.Comment($"keep-alive {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            await _stream.WriteAsync(bytes, linkedCts.Token);
            await _stream.FlushAsync(linkedCts.Token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Start automatic keep-alive pings
    /// </summary>
    public Task StartKeepAliveAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(interval, linkedCts.Token);
                    await WriteKeepAliveAsync(linkedCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }, cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SseStream));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        
        await _stream.FlushAsync();
    }
}

/// <summary>
/// Strongly-typed SSE stream writer for type-safe event streaming
/// </summary>
/// <typeparam name="TEventData">The type of data to send in events</typeparam>
public sealed class TypedSseStream<TEventData>
{
    private readonly SseStream _innerStream;

    public TypedSseStream(SseStream innerStream)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    }

    /// <summary>
    /// Write typed event data with default "message" event type
    /// </summary>
    public Task WriteAsync(TEventData data, CancellationToken cancellationToken = default)
    {
        return _innerStream.WriteJsonAsync(data, cancellationToken);
    }

    /// <summary>
    /// Write typed event data with custom event type
    /// </summary>
    public Task WriteAsync(string eventType, TEventData data, CancellationToken cancellationToken = default)
    {
        return _innerStream.WriteJsonAsync(eventType, data, cancellationToken);
    }

    /// <summary>
    /// Write keep-alive comment
    /// </summary>
    public Task WriteKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        return _innerStream.WriteKeepAliveAsync(cancellationToken);
    }

    /// <summary>
    /// Start automatic keep-alive pings
    /// </summary>
    public Task StartKeepAliveAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        return _innerStream.StartKeepAliveAsync(interval, cancellationToken);
    }
}
