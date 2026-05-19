using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using EffinitiveFramework.Core.Transport;
using Microsoft.Extensions.ObjectPool;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Represents a single HTTP connection with pipeline support.
/// For plaintext HTTP/1.1, uses the high-performance SocketTransportConnection
/// with separated read/write loops, pooled SocketAsyncEventArgs, and IOQueue scheduling.
/// For TLS connections, falls back to SslStream + PipeReader/PipeWriter.
/// </summary>
public sealed class HttpConnection : IDisposable, IAsyncDisposable
{
    private Socket? _socket;
    private Stream? _stream;
    private PipeReader? _reader;
    private PipeWriter? _writer;
    private bool _isSecure;
    private CancellationTokenSource? _timeoutCts;

    // High-performance transport (plaintext only)
    private SocketTransportConnection? _transport;
    
    // Cached TLS options — shared across all connections to avoid per-connection allocation
    private static SslServerAuthenticationOptions? _cachedSslOptions;
    private static readonly object _sslOptionsLock = new();

    public bool IsConnected => _socket?.Connected ?? false;
    public DateTime LastActivity { get; private set; }
    public string? NegotiatedProtocol { get; private set; }
    public Stream? Stream => _stream;

    /// <summary>
    /// Get a Stream for this connection — returns the underlying SslStream/NetworkStream
    /// if available, otherwise wraps the PipeWriter/PipeReader as a Stream for WebSocket use.
    /// </summary>
    public Stream? GetOrCreateStream()
    {
        if (_stream != null) return _stream;
        if (_reader != null && _writer != null)
            return new DuplexPipeStream(_reader, _writer);
        return null;
    }

    /// <summary>
    /// Initialize connection with a socket (public API — legacy/TLS path).
    /// </summary>
    public Task InitializeAsync(
        Socket socket,
        bool isSecure,
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
        => InitializeAsync(socket, isSecure, certificate, cancellationToken, null, null);

    /// <summary>
    /// Initialize connection with a socket using the high-performance transport.
    /// </summary>
    internal async Task InitializeAsync(
        Socket socket,
        bool isSecure,
        X509Certificate2? certificate,
        CancellationToken cancellationToken,
        PipeScheduler? ioScheduler,
        SocketSenderPool? senderPool)
    {
        _socket = socket;
        _isSecure = isSecure;
        LastActivity = DateTime.UtcNow;

        if (isSecure && certificate != null)
        {
            // TLS connections go through SslStream — can't use direct socket transport
            var networkStream = new NetworkStream(_socket, ownsSocket: false);
            var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            
            // Reuse cached SslServerAuthenticationOptions to avoid per-connection allocation
            var sslOptions = _cachedSslOptions;
            if (sslOptions == null || sslOptions.ServerCertificate != certificate)
            {
                lock (_sslOptionsLock)
                {
                    if (_cachedSslOptions == null || _cachedSslOptions.ServerCertificate != certificate)
                    {
                        _cachedSslOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                            AllowTlsResume = true,
                        };
                    }
                    sslOptions = _cachedSslOptions;
                }
            }
            
            await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);
            
            NegotiatedProtocol = sslStream.NegotiatedApplicationProtocol.Protocol.Length > 0
                ? System.Text.Encoding.ASCII.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.Span)
                : "http/1.1";

            _stream = sslStream;

            // TLS HTTP/1.1 still uses stream-based pipes
            if (NegotiatedProtocol != "h2")
            {
                _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(
                    bufferSize: 65536,
                    leaveOpen: false));
                _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(
                    minimumBufferSize: 65536,
                    leaveOpen: false));
            }
        }
        else if (ioScheduler != null && senderPool != null)
        {
            // Plaintext HTTP/1.1 with high-performance transport:
            // - Separated read/write loops (writes never block reads)
            // - Pooled SocketAsyncEventArgs (zero-alloc I/O)
            // - IOQueue scheduling (batched continuations)
            // - Zero-byte receive on idle (no buffer until data arrives)
            // - Scatter/gather sends (no copy for multi-segment PipeWriter output)
            var inputOptions = new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.ThreadPool,
                writerScheduler: ioScheduler,
                pauseWriterThreshold: 1024 * 1024,      // 1 MiB input backpressure
                resumeWriterThreshold: 512 * 1024,       // resume at 512 KiB
                useSynchronizationContext: false);

            var outputOptions = new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: ioScheduler,
                writerScheduler: PipeScheduler.ThreadPool,
                pauseWriterThreshold: 1024 * 1024,       // 1 MiB output backpressure
                resumeWriterThreshold: 512 * 1024,
                useSynchronizationContext: false);

            _transport = new SocketTransportConnection(
                socket, ioScheduler, senderPool, inputOptions, outputOptions,
                waitForData: false);

            _reader = _transport.Application.Input;
            _writer = _transport.Application.Output;

            _transport.Start();
        }
        else
        {
            // Fallback: no IOQueue available — use NetworkStream (legacy path)
            _stream = new NetworkStream(_socket, ownsSocket: false);

            _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(
                bufferSize: 65536,
                leaveOpen: false));
            _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(
                minimumBufferSize: 65536,
                leaveOpen: false));
        }
    }

    /// <summary>
    /// Create a streaming body reader for a deferred (large) request body.
    /// The caller is responsible for draining it before the next request.
    /// </summary>
    internal PipeReaderBodyStream CreateBodyStream(long contentLength)
        => new PipeReaderBodyStream(_reader!, contentLength);

    /// <summary>
    /// Read and parse HTTP request
    /// </summary>
    public async ValueTask<HttpRequest?> ReadRequestAsync(
        TimeSpan headerTimeout,
        int maxBodySize,
        CancellationToken cancellationToken)
    {
        if (_reader == null)
            return null;

        // Reuse a single CTS per connection instead of allocating a new linked CTS per read.
        _timeoutCts ??= new CancellationTokenSource();
        _timeoutCts.CancelAfter(headerTimeout);

        try
        {
            var request = new HttpRequest();

            while (true)
            {
                var result = await _reader.ReadAsync(_timeoutCts.Token);
                var buffer = result.Buffer;
                var originalBuffer = buffer;

                try
                {
                    if (HttpRequestParser.TryParseRequest(
                        ref buffer,
                        request,
                        out var consumed,
                        out _,
                        maxBodySize))
                    {
                        _reader.AdvanceTo(consumed);
                        LastActivity = DateTime.UtcNow;
                        // Reset the CTS for the next read instead of disposing
                        TryResetTimeoutCts();
                        return request;
                    }
                }
                catch (HttpParseException ex)
                {
                    var keepAliveAllowed =
                        ShouldKeepAliveAfterParseError(ex.Message) &&
                        TryDiscardMalformedRequest(buffer, originalBuffer);
                    throw new HttpParseException(ex.StatusCode, ex.Message, keepAliveAllowed);
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    return null; // Connection closed
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds maximum allowed size"))
        {
            // Payload Too Large - return null to close connection
            return null;
        }
        catch (OperationCanceledException)
        {
            // Timeout
            return null;
        }
        finally
        {
            TryResetTimeoutCts();
        }
    }

    private bool TryDiscardMalformedRequest(ReadOnlySequence<byte> unreadBuffer, ReadOnlySequence<byte> originalBuffer)
    {
        if (_reader == null)
            return false;

        if (!unreadBuffer.IsEmpty && TryFindHeaderTerminator(unreadBuffer, out var consumed))
        {
            _reader.AdvanceTo(consumed);
            LastActivity = DateTime.UtcNow;
            return true;
        }

        if (unreadBuffer.Start.Equals(originalBuffer.Start))
            return false;

        _reader.AdvanceTo(unreadBuffer.Start);
        LastActivity = DateTime.UtcNow;
        return true;
    }

    private static bool TryFindHeaderTerminator(ReadOnlySequence<byte> buffer, out SequencePosition consumed)
    {
        var span = buffer.ToArray().AsSpan();

        var idx = span.IndexOf("\r\n\r\n"u8);
        var terminatorLength = 4;

        if (idx < 0)
        {
            idx = span.IndexOf("\n\n"u8);
            terminatorLength = 2;
        }

        if (idx < 0)
        {
            consumed = default;
            return false;
        }

        consumed = buffer.GetPosition(idx + terminatorLength);
        return true;
    }

    private static bool ShouldKeepAliveAfterParseError(string message)
    {
        return message.StartsWith("Space before colon", StringComparison.Ordinal) ||
               message.StartsWith("Missing Host header", StringComparison.Ordinal) ||
               message.StartsWith("Duplicate Host header", StringComparison.Ordinal) ||
               message.StartsWith("Empty Host header value", StringComparison.Ordinal) ||
               message.StartsWith("Host header contains", StringComparison.Ordinal);
    }

    /// <summary>
    /// Try to reset the timeout CTS for reuse. If it's already been cancelled,
    /// dispose it and let the next call create a fresh one.
    /// </summary>
    private void TryResetTimeoutCts()
    {
        if (_timeoutCts == null) return;
        if (!_timeoutCts.TryReset())
        {
            _timeoutCts.Dispose();
            _timeoutCts = null;
        }
    }

    /// <summary>
    /// Write HTTP response
    /// </summary>
    public async ValueTask WriteResponseAsync(
        HttpResponse response,
        CancellationToken cancellationToken,
        bool flush = true)
    {
        if (_writer == null)
            return;

        // For non-streaming responses, _stream isn't required — PipeWriter handles everything
        if (_stream == null && !response.IsStreaming)
        {
            await HttpResponseWriter.WriteResponseAsync(_writer, response, cancellationToken, flush);
            return;
        }

        if (_stream != null)
        {
            await HttpResponseWriter.WriteResponseAsync(_writer, response, cancellationToken, flush);
        
            // If this is a streaming response, execute the stream handler
            if (response.IsStreaming && response.StreamHandler != null)
            {
                await response.StreamHandler(_stream, cancellationToken);
            }
        }
        else
        {
            // Transport-based path: no underlying Stream.
            // Write headers via PipeWriter, then wrap PipeWriter as a Stream for the handler.
            await HttpResponseWriter.WriteResponseAsync(_writer, response, cancellationToken, flush: true);

            if (response.IsStreaming && response.StreamHandler != null)
            {
                await response.StreamHandler(new PipeWriterStreamAdapter(_writer), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Flush all buffered response data to the underlying stream in a single write.
    /// Call this after writing one or more responses with flush: false.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_writer != null)
            await _writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Complete the response writer and wait for the transport send loop to drain
    /// before the socket is closed. Used for final Connection: close responses.
    /// </summary>
    public async ValueTask CloseGracefullyAsync()
    {
        if (_transport == null || _writer == null)
            return;

        await _writer.CompleteAsync();
        await _transport.DisposeAsync();

        _writer = null;
        _reader = null;
        _transport = null;
    }

    /// <summary>
    /// Synchronously try to parse the next request from already-buffered pipe data.
    /// Returns null if the buffer is empty or contains only a partial request.
    /// Does not perform any I/O — only consumes data already in the pipe buffer.
    /// </summary>
    public HttpRequest? TryParseQueuedRequest(int maxBodySize)
    {
        if (_reader == null) return null;
        if (!_reader.TryRead(out var result)) return null;

        var buffer = result.Buffer;
        if (buffer.IsEmpty)
        {
            _reader.AdvanceTo(buffer.Start, buffer.End);
            return null;
        }

        var request = new HttpRequest();
        if (HttpRequestParser.TryParseRequest(
            ref buffer,
            request,
            out var consumed,
            out _,
            maxBodySize))
        {
            _reader.AdvanceTo(consumed);
            LastActivity = DateTime.UtcNow;
            return request;
        }

        // Incomplete request — mark as examined so the next ReadAsync gets fresh data appended
        _reader.AdvanceTo(buffer.Start, buffer.End);
        return null;
    }

    /// <summary>
    /// Reset connection for reuse in pool
    /// </summary>
    public void Reset()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;
        // Keep socket/stream/pipelines for reuse
    }

    /// <summary>
    /// Dispose the connection
    /// </summary>
    public void Dispose()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();

        if (_transport != null)
        {
            // Abort immediately — fire-and-forget task cleanup.
            // DisposeAsync should be preferred; this is a fallback.
            _transport.Abort();
        }
        else
        {
            // Legacy stream-based path
            _reader?.Complete();
            _writer?.Complete();
            _stream?.Dispose();
            _socket?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();

        if (_transport != null)
        {
            await _reader!.CompleteAsync();
            await _writer!.CompleteAsync();
            await _transport.DisposeAsync();
        }
        else
        {
            _reader?.Complete();
            _writer?.Complete();
            if (_stream != null)
                await _stream.DisposeAsync();
            _socket?.Dispose();
        }
    }
}

/// <summary>
/// Wraps a PipeWriter as a Stream for streaming response handlers.
/// Used when the transport layer bypasses NetworkStream (direct socket I/O).
/// </summary>
internal sealed class PipeWriterStreamAdapter(PipeWriter writer) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        writer.Write(buffer.AsSpan(offset, count));
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        writer.Write(buffer);
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        writer.Write(buffer.AsSpan(offset, count));
        await writer.FlushAsync(cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        writer.Write(buffer.Span);
        await writer.FlushAsync(cancellationToken);
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await writer.FlushAsync(cancellationToken);
    }

    public override void Flush() => writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Wraps a PipeReader + PipeWriter as a duplex Stream.
/// Used for WebSocket upgrade when the connection uses the direct socket transport (no SslStream).
/// </summary>
internal sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var data = result.Buffer;

        if (data.IsEmpty && result.IsCompleted)
            return 0;

        var bytesToCopy = (int)Math.Min(data.Length, buffer.Length);
        data.Slice(0, bytesToCopy).CopyTo(buffer.Span);
        reader.AdvanceTo(data.GetPosition(bytesToCopy));
        return bytesToCopy;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        writer.Write(buffer.Span);
        await writer.FlushAsync(cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        writer.Write(buffer.AsSpan(offset, count));
        await writer.FlushAsync(cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        writer.Write(buffer.AsSpan(offset, count));
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken) => await writer.FlushAsync(cancellationToken);
    public override void Flush() => writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Object pool policy for HttpConnection
/// </summary>
public sealed class HttpConnectionPoolPolicy : IPooledObjectPolicy<HttpConnection>
{
    public HttpConnection Create()
    {
        return new HttpConnection();
    }

    public bool Return(HttpConnection obj)
    {
        if (!obj.IsConnected)
        {
            obj.Dispose();
            return false; // Don't return to pool
        }

        obj.Reset();
        return true; // Return to pool for reuse
    }
}
