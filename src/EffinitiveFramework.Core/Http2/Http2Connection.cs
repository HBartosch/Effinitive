using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2.Hpack;
using System.Threading.Channels;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Manages an HTTP/2 connection
/// </summary>
public class Http2Connection : IAsyncDisposable
{
    // Buffer queued for the single writer task. IsPooled signals ArrayPool ownership.
    private readonly record struct PooledBuffer(byte[] Data, int Length, bool IsPooled)
    {
        public Memory<byte> AsMemory() => Data.AsMemory(0, Length);
        public void Return() { if (IsPooled) ArrayPool<byte>.Shared.Return(Data); }
    }

    private readonly Stream _stream;
    private readonly ConcurrentDictionary<int, Http2Stream> _streams = new();
    private readonly ConcurrentDictionary<int, Http2Stream> _pushedStreams = new();
    // Single writer task drains this channel — frame reader and stream handlers
    // never block waiting to write, which prevents the frame reader from stalling
    // while response tasks hold the old write lock.
    private readonly Channel<PooledBuffer> _writeChannel =
        Channel.CreateUnbounded<PooledBuffer>(new UnboundedChannelOptions { SingleReader = true });
    private readonly StreamPriorityScheduler _priorityScheduler = new();
    private HpackDecoder _hpackDecoder;
    private readonly HpackEncoder _hpackEncoder = new();
    private readonly Func<HttpRequest, Task<HttpResponse>>? _requestHandler;

    // Connection flow control synchronization
    private readonly object _windowLock = new();
    
    // Connection settings
    private uint _headerTableSize = Http2Constants.DefaultHeaderTableSize;
    private uint _enablePush = Http2Constants.DefaultEnablePush;
    private uint _maxConcurrentStreams = Http2Constants.DefaultMaxConcurrentStreams;
    private uint _initialWindowSize = Http2Constants.DefaultInitialWindowSize;
    private uint _maxFrameSize = Http2Constants.DefaultMaxFrameSize;
    private uint _maxHeaderListSize = Http2Constants.DefaultMaxHeaderListSize;
    
    // Connection flow control
    private int _connectionWindowSize = (int)Http2Constants.DefaultInitialWindowSize;
    
    // State
    private bool _prefaceReceived;
    private bool _settingsAckReceived;
    private int _lastStreamId;
    private int _nextPushStreamId = 2; // Server-initiated streams use even IDs
    private int _pushedStreamCount = 0; // Track number of pushed streams
    
    // WINDOW_UPDATE batching: accumulate small increments, send when threshold reached
    private int _pendingConnectionWindowUpdate;
    private const int WindowUpdateThreshold = 65536; // Send WINDOW_UPDATE when accumulated >= 64KB
    
    // Push limits (configurable via ServerOptions)
    private readonly int _maxPushedStreams;
    private readonly int _maxPushedResourceSize;
    
    public Http2Connection(Stream stream, Func<HttpRequest, Task<HttpResponse>>? requestHandler = null, 
        int maxPushedStreams = 10, int maxPushedResourceSize = 1024 * 1024)
    {
        _stream = stream;
        _requestHandler = requestHandler;
        _maxPushedStreams = maxPushedStreams;
        _maxPushedResourceSize = maxPushedResourceSize;
        _hpackDecoder = new HpackDecoder((int)_headerTableSize, (int)_maxHeaderListSize);
    }
    
    /// <summary>
    /// Start processing the HTTP/2 connection
    /// </summary>
    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        // Single writer task: all frame writes are queued here so the frame-reading
        // loop is never blocked waiting to send a SETTINGS ACK or PING ACK while
        // response tasks are occupying the old write lock.
        var writerTask = DrainWriteChannelAsync(cancellationToken);

        try
        {
            if (!await ReceiveClientPrefaceAsync(cancellationToken))
            {
                await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                return;
            }

            await SendSettingsAsync(cancellationToken);

            // RFC 7540 §6.9.2: advertise a larger connection window (initial is 65535)
            var windowIncrement = (int)_initialWindowSize - 65535;
            if (windowIncrement > 0)
                await SendWindowUpdateAsync(0, windowIncrement, cancellationToken);

            await ProcessFramesAsync(cancellationToken);
        }
        catch (Exception)
        {
            try { await SendGoAwayAsync(Http2Constants.ErrorInternalError, cancellationToken); }
            catch { /* best effort */ }
        }
        finally
        {
            // Signal writer that no more frames are coming, then wait for it to drain.
            _writeChannel.Writer.TryComplete();
            try { await writerTask; } catch { /* drainer already swallows errors */ }
        }
    }

    /// <summary>
    /// Drains the write channel, batching socket writes and flushing when the
    /// channel is momentarily empty.  This is the only task that writes to _stream.
    /// </summary>
    private async Task DrainWriteChannelAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _writeChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                // Drain everything that is immediately available in one batch.
                while (_writeChannel.Reader.TryRead(out var item))
                {
                    await _stream.WriteAsync(item.AsMemory(), cancellationToken);
                    item.Return();
                }
                // One flush per batch keeps TLS records coalesced.
                await _stream.FlushAsync(cancellationToken);
            }
            // Final flush for any data written before TryComplete().
            await _stream.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            // Return any buffers left in the channel (e.g. after cancellation).
            while (_writeChannel.Reader.TryRead(out var item))
                item.Return();
        }
    }
    
    /// <summary>
    /// Push a resource to the client (server push)
    /// </summary>
    /// <param name="associatedStreamId">The stream ID of the request that triggered this push</param>
    /// <param name="requestHeaders">The request headers for the pushed resource (e.g., :method, :path, :authority)</param>
    /// <param name="responseHeaders">The response headers for the pushed resource</param>
    /// <param name="responseBody">The response body data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task PushResourceAsync(
        int associatedStreamId, 
        Dictionary<string, string> requestHeaders,
        Dictionary<string, string> responseHeaders,
        ReadOnlyMemory<byte> responseBody,
        CancellationToken cancellationToken = default)
    {
        // Security check: Verify push is enabled by client (RFC 7540 §8.2)
        if (_enablePush == 0)
            throw new InvalidOperationException("Server push is disabled by client");
        
        // Security limit: Maximum pushed streams per connection
        if (_pushedStreamCount >= _maxPushedStreams)
            throw new InvalidOperationException($"Maximum pushed streams ({_maxPushedStreams}) exceeded");
        
        // Security limit: Maximum pushed resource size
        if (responseBody.Length > _maxPushedResourceSize)
            throw new InvalidOperationException($"Pushed resource size ({responseBody.Length} bytes) exceeds maximum ({_maxPushedResourceSize} bytes)");
        
        // RFC 7540 §8.2: Validate required pseudo-headers
        var requiredHeaders = new[] { ":method", ":scheme", ":authority", ":path" };
        foreach (var header in requiredHeaders)
        {
            if (!requestHeaders.ContainsKey(header))
                throw new InvalidOperationException($"Push request missing required pseudo-header: {header}");
        }
        
        // RFC 7540 §8.2: Validate method is safe (GET or HEAD only)
        if (!requestHeaders.TryGetValue(":method", out var method))
            throw new InvalidOperationException("Push request missing :method header");
        
        if (method != "GET" && method != "HEAD")
            throw new InvalidOperationException($"Push only supports safe methods (GET, HEAD), got: {method}");
        
        // Allocate new even stream ID for the pushed stream
        var promisedStreamId = Interlocked.Add(ref _nextPushStreamId, 2);
        Interlocked.Increment(ref _pushedStreamCount);
        
        // Send PUSH_PROMISE frame on the associated stream
        await SendPushPromiseAsync(associatedStreamId, promisedStreamId, requestHeaders, cancellationToken);
        
        // Create the pushed stream
        var pushedStream = new Http2Stream(promisedStreamId, (int)_initialWindowSize);
        _pushedStreams.TryAdd(promisedStreamId, pushedStream);
        
        // Send HEADERS frame on the promised stream with response headers
        await SendHeadersAsync(promisedStreamId, responseHeaders, false, cancellationToken);
        
        // RFC 7540 §6.9: Check flow control window before sending DATA
        if (responseBody.Length > 0)
        {
            if (responseBody.Length > pushedStream.WindowSize)
                throw new InvalidOperationException($"Pushed data ({responseBody.Length} bytes) exceeds stream flow control window ({pushedStream.WindowSize} bytes)");
            
            lock (_windowLock)
            {
                if (responseBody.Length > _connectionWindowSize)
                    throw new InvalidOperationException($"Pushed data ({responseBody.Length} bytes) exceeds connection flow control window ({_connectionWindowSize} bytes)");
                
                _connectionWindowSize -= responseBody.Length;
            }
            
            // Update stream flow control window
            pushedStream.UpdateWindowSize(-responseBody.Length);
            
            await SendDataAsync(promisedStreamId, responseBody, true, cancellationToken);
        }
        else
        {
            // If no body, send empty DATA frame with END_STREAM
            await SendDataAsync(promisedStreamId, ReadOnlyMemory<byte>.Empty, true, cancellationToken);
        }
    }
    
    /// <summary>
    /// Receive and validate client preface
    /// </summary>
    private async Task<bool> ReceiveClientPrefaceAsync(CancellationToken cancellationToken)
    {
        var prefaceLength = Http2Constants.ClientPreface.Length;
        var buffer = new byte[prefaceLength];
        
        if (!await ReadExactlyAsync(buffer, 0, prefaceLength, cancellationToken))
            return false;
        
        if (!buffer.AsSpan().SequenceEqual(Http2Constants.ClientPreface))
            return false;
        
        _prefaceReceived = true;
        return true;
    }
    
    /// <summary>
    /// Send SETTINGS frame
    /// </summary>
    private async Task SendSettingsAsync(CancellationToken cancellationToken)
    {
        // RFC 7540 §6.5.2: Server MUST NOT send ENABLE_PUSH in its SETTINGS
        var settings = new (ushort id, uint value)[]
        {
            (Http2Constants.SettingsHeaderTableSize, _headerTableSize),
            (Http2Constants.SettingsMaxConcurrentStreams, _maxConcurrentStreams),
            (Http2Constants.SettingsInitialWindowSize, _initialWindowSize),
            (Http2Constants.SettingsMaxFrameSize, _maxFrameSize),
            (Http2Constants.SettingsMaxHeaderListSize, _maxHeaderListSize),
        };
        
        var payloadLength = settings.Length * 6; // 2 bytes ID + 4 bytes value
        var frameBuffer = new byte[Http2Constants.FrameHeaderLength + payloadLength];
        
        var frame = new Http2Frame
        {
            Length = payloadLength,
            Type = Http2Constants.FrameTypeSettings,
            Flags = 0,
            StreamId = Http2Constants.ConnectionStreamId
        };
        
        frame.WriteHeader(frameBuffer);

        var offset = Http2Constants.FrameHeaderLength;
        foreach (var (id, value) in settings)
        {
            frameBuffer[offset++] = (byte)(id >> 8);
            frameBuffer[offset++] = (byte)id;
            frameBuffer[offset++] = (byte)(value >> 24);
            frameBuffer[offset++] = (byte)(value >> 16);
            frameBuffer[offset++] = (byte)(value >> 8);
            frameBuffer[offset++] = (byte)value;
        }

        await _writeChannel.Writer.WriteAsync(
            new PooledBuffer(frameBuffer, frameBuffer.Length, IsPooled: false), cancellationToken);
    }
    
    /// <summary>
    /// Process incoming frames
    /// </summary>
    private async Task ProcessFramesAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[Http2Constants.FrameHeaderLength];
        
        while (!cancellationToken.IsCancellationRequested)
        {
            // Read frame header (must read all 9 bytes)
            if (!await ReadExactlyAsync(headerBuffer, 0, Http2Constants.FrameHeaderLength, cancellationToken))
            {
                break; // Connection closed
            }
            
            if (!Http2Frame.TryParseHeader(headerBuffer, out var frame))
            {
                await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                break;
            }
            
            // Read frame payload if present
            byte[]? rentedBuffer = null;
            if (frame.Length > 0)
            {
                // SECURITY: Validate frame size doesn't exceed max (prevents DoS)
                if (frame.Length > _maxFrameSize)
                {
                    await SendGoAwayAsync(Http2Constants.ErrorFrameSizeError, cancellationToken);
                    break;
                }
                
                // Use ArrayPool to reduce allocations on the hot path
                rentedBuffer = ArrayPool<byte>.Shared.Rent(frame.Length);
                if (!await ReadExactlyAsync(rentedBuffer, 0, frame.Length, cancellationToken))
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    await SendGoAwayAsync(Http2Constants.ErrorFrameSizeError, cancellationToken);
                    break;
                }
                
                frame.Payload = rentedBuffer.AsMemory(0, frame.Length);
            }
            
            try
            {
                // Process frame based on type
                await ProcessFrameAsync(frame, cancellationToken);
            }
            finally
            {
                // Return rented buffer after processing
                if (rentedBuffer != null)
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }
    
    /// <summary>
    /// Read exactly <paramref name="count"/> bytes from the stream.
    /// Returns false when the connection is closed before any data arrives.
    /// </summary>
    private async Task<bool> ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = await _stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken);
            if (bytesRead == 0)
                return totalRead == 0 ? false : throw new IOException("Connection closed mid-frame");
            totalRead += bytesRead;
        }
        return true;
    }
    
    /// <summary>
    /// Process a single frame
    /// </summary>
    private async Task ProcessFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case Http2Constants.FrameTypeSettings:
                await ProcessSettingsFrameAsync(frame, cancellationToken);
                break;
            
            case Http2Constants.FrameTypeHeaders:
                await ProcessHeadersFrameAsync(frame, cancellationToken);
                break;
            
            case Http2Constants.FrameTypeData:
                await ProcessDataFrameAsync(frame, cancellationToken);
                break;
            
            case Http2Constants.FrameTypeWindowUpdate:
                await ProcessWindowUpdateFrameAsync(frame, cancellationToken);
                break;
            
            case Http2Constants.FrameTypePing:
                await ProcessPingFrameAsync(frame, cancellationToken);
                break;
            
            case Http2Constants.FrameTypeRstStream:
                await ProcessRstStreamFrameAsync(frame, cancellationToken);
                break;
            
            case Http2Constants.FrameTypePriority:
                // Priority frames are advisory — acknowledge but don't block
                break;
            
            case Http2Constants.FrameTypeGoAway:
                // Connection closing
                break;
            
            case Http2Constants.FrameTypeContinuation:
                // CONTINUATION frames outside a header block sequence are a protocol error
                await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                break;
            
            default:
                // Unknown frame type - ignore per spec
                break;
        }
    }
    
    private async Task ProcessSettingsFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (frame.HasFlag(Http2Constants.FlagAck))
        {
            // Settings ACK received
            _settingsAckReceived = true;
            return;
        }
        
        // Parse settings
        var payload = frame.Payload.Span;
        for (int i = 0; i < payload.Length; i += 6)
        {
            var id = (ushort)((payload[i] << 8) | payload[i + 1]);
            var value = (uint)((payload[i + 2] << 24) | (payload[i + 3] << 16) | (payload[i + 4] << 8) | payload[i + 5]);
            
            switch (id)
            {
                case Http2Constants.SettingsHeaderTableSize:
                    _headerTableSize = value;
                    _hpackDecoder = new HpackDecoder((int)value, (int)_maxHeaderListSize);
                    break;
                case Http2Constants.SettingsEnablePush:
                    // RFC 7540: MUST be 0 or 1
                    if (value > 1)
                    {
                        await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                        return;
                    }
                    _enablePush = value;
                    break;
                case Http2Constants.SettingsMaxConcurrentStreams:
                    _maxConcurrentStreams = value;
                    break;
                case Http2Constants.SettingsInitialWindowSize:
                    // RFC 7540: MUST NOT exceed 2^31-1 (2147483647)
                    if (value > 2147483647)
                    {
                        await SendGoAwayAsync(Http2Constants.ErrorFlowControlError, cancellationToken);
                        return;
                    }
                    _initialWindowSize = value;
                    break;
                case Http2Constants.SettingsMaxFrameSize:
                    // RFC 7540: MUST be between 2^14 (16384) and 2^24-1 (16777215)
                    if (value < 16384 || value > 16777215)
                    {
                        await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                        return;
                    }
                    _maxFrameSize = value;
                    break;
                case Http2Constants.SettingsMaxHeaderListSize:
                    _maxHeaderListSize = value;
                    break;
            }
        }
        
        // Send SETTINGS ACK
        await SendSettingsAckAsync(cancellationToken);
    }
    
    private async Task ProcessHeadersFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.StreamId;
        
        if (streamId == 0)
        {
            // HEADERS on stream 0 is a protocol error
            await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
            return;
        }
        
        // RFC 7540 §5.1.1: Client-initiated streams MUST use odd IDs
        if (streamId % 2 == 0)
        {
            // Even stream IDs are reserved for server-initiated streams
            await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
            return;
        }
        
        // SECURITY: Enforce max concurrent streams including pushed streams (prevents resource exhaustion)
        var totalStreams = _streams.Count + _pushedStreams.Count;
        if (totalStreams >= _maxConcurrentStreams && !_streams.ContainsKey(streamId))
        {
            await SendRstStreamAsync(streamId, Http2Constants.ErrorRefusedStream, cancellationToken);
            return;
        }
        
        // Get or create stream
        var stream = _streams.GetOrAdd(streamId, id => new Http2Stream(id, (int)_initialWindowSize));
        
        if (stream.State == Http2StreamState.Idle)
        {
            stream.UpdateState(Http2StreamState.Open);
            _lastStreamId = streamId;
        }
        
        // Parse padding if present
        var payloadOffset = 0;
        if (frame.HasFlag(Http2Constants.FlagPadded))
        {
            var padLength = frame.Payload.Span[0];
            payloadOffset = 1;
            // Reduce payload by padding
        }
        
        // Skip priority if present
        Http2StreamPriority? priority = null;
        if (frame.HasFlag(Http2Constants.FlagPriority))
        {
            var priorityData = frame.Payload.Slice(payloadOffset, 5);
            priority = Http2StreamPriority.Parse(priorityData.Span);
            payloadOffset += 5; // Stream dependency (4 bytes) + weight (1 byte)
        }
        
        // Register stream with priority scheduler
        _priorityScheduler.RegisterStream(streamId, priority);
        
        // Collect header block fragments (may span CONTINUATION frames)
        var headerBlock = frame.Payload.Slice(payloadOffset);
        
        // If END_HEADERS is not set, we need to read CONTINUATION frames
        if (!frame.HasFlag(Http2Constants.FlagEndHeaders))
        {
            using var headerAccumulator = new MemoryStream();
            headerAccumulator.Write(headerBlock.Span);
            
            // Read CONTINUATION frames until END_HEADERS
            var contHeaderBuf = new byte[Http2Constants.FrameHeaderLength];
            while (true)
            {
                if (!await ReadExactlyAsync(contHeaderBuf, 0, Http2Constants.FrameHeaderLength, cancellationToken))
                {
                    await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                    return;
                }
                
                if (!Http2Frame.TryParseHeader(contHeaderBuf, out var contFrame))
                {
                    await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                    return;
                }
                
                // Must be CONTINUATION on the same stream
                if (contFrame.Type != Http2Constants.FrameTypeContinuation || contFrame.StreamId != streamId)
                {
                    await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                    return;
                }
                
                if (contFrame.Length > 0)
                {
                    var contPayload = ArrayPool<byte>.Shared.Rent(contFrame.Length);
                    try
                    {
                        if (!await ReadExactlyAsync(contPayload, 0, contFrame.Length, cancellationToken))
                        {
                            await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                            return;
                        }
                        headerAccumulator.Write(contPayload, 0, contFrame.Length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(contPayload);
                    }
                }
                
                if (contFrame.HasFlag(Http2Constants.FlagEndHeaders))
                    break;
            }
            
            headerBlock = headerAccumulator.ToArray();
        }
        
        // Decode HPACK headers
        List<(string name, string value)> headers;
        try
        {
            headers = _hpackDecoder.DecodeHeaders(headerBlock.Span);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HPACK decompression"))
        {
            // HPACK bomb detected - send COMPRESSION_ERROR
            await SendGoAwayAsync(Http2Constants.ErrorCompressionError, cancellationToken);
            return;
        }
        
        // SECURITY: Validate total header list size (prevents header flooding DoS)
        int totalHeaderSize = 0;
        foreach (var (name, value) in headers)
        {
            totalHeaderSize += name.Length + value.Length;
        }
        
        if (totalHeaderSize > _maxHeaderListSize)
        {
            await SendRstStreamAsync(streamId, Http2Constants.ErrorRefusedStream, cancellationToken);
            return;
        }
        
        // Add headers to stream
        foreach (var (name, value) in headers)
        {
            stream.AddHeader(name, value);
        }
        
        // If END_HEADERS flag is set (or we collected all via CONTINUATION), process the request
        if (frame.HasFlag(Http2Constants.FlagEndStream) || frame.HasFlag(Http2Constants.FlagEndHeaders))
        {
            if (frame.HasFlag(Http2Constants.FlagEndStream))
            {
                // No body expected — dispatch concurrently for stream multiplexing
                stream.UpdateState(Http2StreamState.HalfClosedRemote);
                _ = ProcessStreamRequestAsync(stream, cancellationToken);
            }
            // Otherwise, wait for DATA frames
        }
    }
    
    private async Task ProcessDataFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.StreamId;
        
        if (streamId == 0)
        {
            // DATA on stream 0 is a protocol error
            await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
            return;
        }
        
        if (!_streams.TryGetValue(streamId, out var stream))
        {
            // Stream doesn't exist - send RST_STREAM
            await SendRstStreamAsync(streamId, Http2Constants.ErrorStreamClosed, cancellationToken);
            return;
        }
        
        // Parse padding if present
        var payloadOffset = 0;
        var dataLength = frame.Length;
        
        if (frame.HasFlag(Http2Constants.FlagPadded))
        {
            var padLength = frame.Payload.Span[0];
            payloadOffset = 1;
            dataLength -= (1 + padLength);
        }
        
        // Append data to stream
        if (dataLength > 0)
        {
            var data = frame.Payload.Slice(payloadOffset, dataLength);
            stream.AppendData(data.Span);
            
            // Batch WINDOW_UPDATEs: accumulate and send when threshold is reached
            _pendingConnectionWindowUpdate += dataLength;
            if (_pendingConnectionWindowUpdate >= WindowUpdateThreshold)
            {
                var increment = _pendingConnectionWindowUpdate;
                _pendingConnectionWindowUpdate = 0;
                await SendWindowUpdateAsync(0, increment, cancellationToken); // Connection level
            }
            await SendWindowUpdateAsync(streamId, dataLength, cancellationToken); // Stream level
        }
        
        // If END_STREAM, dispatch the request concurrently
        if (frame.HasFlag(Http2Constants.FlagEndStream))
        {
            stream.UpdateState(Http2StreamState.HalfClosedRemote);
            _ = ProcessStreamRequestAsync(stream, cancellationToken);
        }
    }
    
    private async Task ProcessStreamRequestAsync(Http2Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            // Convert HTTP/2 headers to HTTP/1.1 request
            var headers = stream.Headers.Select(kvp => (kvp.Key, kvp.Value)).ToList();
            
            // Reset buffer position to read from beginning
            stream.DataBuffer.Position = 0;
            var bodyBytes = stream.DataBuffer.ToArray();
            
            var request = Http2RequestConverter.ConvertToHttp1Request(headers, bodyBytes);
            
            // Process request through handler
            HttpResponse response;
            if (_requestHandler != null)
            {
                response = await _requestHandler(request);
            }
            else
            {
                // Default response
                response = new HttpResponse
                {
                    StatusCode = 200,
                    Body = "HTTP/2 works!"u8.ToArray(),
                    ContentType = "text/plain"
                };
            }
            
            // Send response
            await SendResponseAsync(stream.StreamId, response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Connection shutting down — don't send RST_STREAM
        }
        catch (IOException)
        {
            // Connection broken — don't try to write
        }
        catch (ChannelClosedException)
        {
            // Write channel completed (connection closing) — don't send RST_STREAM
        }
        catch (Exception)
        {
            try
            {
                await SendRstStreamAsync(stream.StreamId, Http2Constants.ErrorInternalError, cancellationToken);
            }
            catch
            {
                // Best effort — connection may already be dead
            }
        }
        finally
        {
            // Always clean up the stream
            stream.UpdateState(Http2StreamState.Closed);
            _streams.TryRemove(stream.StreamId, out _);
        }
    }
    
    private async Task SendResponseAsync(int streamId, HttpResponse response, CancellationToken cancellationToken)
    {
        response.MaterializeDeferredBody();

        var headers = Http2ResponseConverter.ConvertToHttp2Headers(response);
        // HpackEncoder is stateless (no dynamic table), so concurrent calls are safe.
        var encodedHeaders = _hpackEncoder.EncodeHeaders(headers);

        if (response.Body == null || response.Body.Length == 0)
        {
            // HEADERS with END_HEADERS + END_STREAM (no body)
            var totalLen = Http2Constants.FrameHeaderLength + encodedHeaders.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(totalLen);
            new Http2Frame
            {
                Length = encodedHeaders.Length,
                Type = Http2Constants.FrameTypeHeaders,
                Flags = (byte)(Http2Constants.FlagEndHeaders | Http2Constants.FlagEndStream),
                StreamId = streamId
            }.WriteHeader(buffer);
            encodedHeaders.AsSpan().CopyTo(buffer.AsSpan(Http2Constants.FrameHeaderLength));

            // Queue and return — writer task owns the buffer lifecycle.
            await _writeChannel.Writer.WriteAsync(new PooledBuffer(buffer, totalLen, IsPooled: true), cancellationToken);
        }
        else
        {
            // Combined HEADERS + DATA in one contiguous buffer (single write syscall).
            var bodyLen = response.Body.Length;
            var headersFrameLen = Http2Constants.FrameHeaderLength + encodedHeaders.Length;
            var totalLen = headersFrameLen + Http2Constants.FrameHeaderLength + bodyLen;
            var combined = ArrayPool<byte>.Shared.Rent(totalLen);

            new Http2Frame
            {
                Length = encodedHeaders.Length,
                Type = Http2Constants.FrameTypeHeaders,
                Flags = Http2Constants.FlagEndHeaders,
                StreamId = streamId
            }.WriteHeader(combined);
            encodedHeaders.AsSpan().CopyTo(combined.AsSpan(Http2Constants.FrameHeaderLength));

            new Http2Frame
            {
                Length = bodyLen,
                Type = Http2Constants.FrameTypeData,
                Flags = Http2Constants.FlagEndStream,
                StreamId = streamId
            }.WriteHeader(combined.AsSpan(headersFrameLen));
            response.Body.AsSpan().CopyTo(combined.AsSpan(headersFrameLen + Http2Constants.FrameHeaderLength));

            await _writeChannel.Writer.WriteAsync(new PooledBuffer(combined, totalLen, IsPooled: true), cancellationToken);
        }
    }
    
    private Task ProcessWindowUpdateFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        var increment = (frame.Payload.Span[0] << 24) | (frame.Payload.Span[1] << 16) | 
                       (frame.Payload.Span[2] << 8) | frame.Payload.Span[3];
        increment &= 0x7FFFFFFF; // Clear reserved bit
        
        if (frame.StreamId == 0)
        {
            // Connection-level window update
            lock (_windowLock)
            {
                _connectionWindowSize += increment;
            }
        }
        else if (_streams.TryGetValue(frame.StreamId, out var stream))
        {
            // Stream-level window update
            stream.UpdateWindowSize(increment);
        }
        
        return Task.CompletedTask;
    }
    
    private async Task ProcessPingFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        // Echo PING with ACK flag
        if (!frame.HasFlag(Http2Constants.FlagAck))
        {
            frame.SetFlag(Http2Constants.FlagAck);
            await SendFrameAsync(frame, cancellationToken);
        }
    }
    
    private Task ProcessRstStreamFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.StreamId;
        
        if (streamId == 0)
        {
            // RST_STREAM on stream 0 is a protocol error
            return SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
        }
        
        // Remove the stream from our dictionary
        if (_streams.TryRemove(streamId, out var stream))
        {
            stream.UpdateState(Http2StreamState.Closed);
        }
        
        return Task.CompletedTask;
    }
    
    private async Task SendSettingsAckAsync(CancellationToken cancellationToken)
    {
        var frame = new Http2Frame
        {
            Length = 0,
            Type = Http2Constants.FrameTypeSettings,
            Flags = Http2Constants.FlagAck,
            StreamId = Http2Constants.ConnectionStreamId
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    private async Task SendGoAwayAsync(uint errorCode, CancellationToken cancellationToken)
    {
        var payloadBuffer = new byte[8];
        
        // Last stream ID (4 bytes)
        payloadBuffer[0] = (byte)(_lastStreamId >> 24);
        payloadBuffer[1] = (byte)(_lastStreamId >> 16);
        payloadBuffer[2] = (byte)(_lastStreamId >> 8);
        payloadBuffer[3] = (byte)_lastStreamId;
        
        // Error code (4 bytes)
        payloadBuffer[4] = (byte)(errorCode >> 24);
        payloadBuffer[5] = (byte)(errorCode >> 16);
        payloadBuffer[6] = (byte)(errorCode >> 8);
        payloadBuffer[7] = (byte)errorCode;
        
        var frame = new Http2Frame
        {
            Length = 8,
            Type = Http2Constants.FrameTypeGoAway,
            Flags = 0,
            StreamId = Http2Constants.ConnectionStreamId,
            Payload = payloadBuffer
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    private async Task SendRstStreamAsync(int streamId, uint errorCode, CancellationToken cancellationToken)
    {
        var payloadBuffer = new byte[4];
        
        // Error code (4 bytes)
        payloadBuffer[0] = (byte)(errorCode >> 24);
        payloadBuffer[1] = (byte)(errorCode >> 16);
        payloadBuffer[2] = (byte)(errorCode >> 8);
        payloadBuffer[3] = (byte)errorCode;
        
        var frame = new Http2Frame
        {
            Length = 4,
            Type = Http2Constants.FrameTypeRstStream,
            Flags = 0,
            StreamId = streamId,
            Payload = payloadBuffer
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    private async Task SendWindowUpdateAsync(int streamId, int increment, CancellationToken cancellationToken)
    {
        var payloadBuffer = new byte[4];
        
        // Window increment (31 bits)
        payloadBuffer[0] = (byte)(increment >> 24);
        payloadBuffer[1] = (byte)(increment >> 16);
        payloadBuffer[2] = (byte)(increment >> 8);
        payloadBuffer[3] = (byte)increment;
        
        var frame = new Http2Frame
        {
            Length = 4,
            Type = Http2Constants.FrameTypeWindowUpdate,
            Flags = 0,
            StreamId = streamId,
            Payload = payloadBuffer
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    /// <summary>
    /// Send PUSH_PROMISE frame
    /// </summary>
    private async Task SendPushPromiseAsync(int streamId, int promisedStreamId, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        // Encode headers using HPACK
        var headerBlock = _hpackEncoder.EncodeHeaders(headers.Select(h => (h.Key, h.Value)).ToList());
        
        // Build payload: 4 bytes promised stream ID + header block
        var payloadLength = 4 + headerBlock.Length;
        var payloadBuffer = new byte[payloadLength];
        
        // Write promised stream ID (31 bits, R bit must be 0)
        payloadBuffer[0] = (byte)(promisedStreamId >> 24);
        payloadBuffer[1] = (byte)(promisedStreamId >> 16);
        payloadBuffer[2] = (byte)(promisedStreamId >> 8);
        payloadBuffer[3] = (byte)promisedStreamId;
        
        // Copy header block
        headerBlock.CopyTo(payloadBuffer.AsSpan(4));
        
        var frame = new Http2Frame
        {
            Length = payloadLength,
            Type = Http2Constants.FrameTypePushPromise,
            Flags = Http2Constants.FlagEndHeaders,
            StreamId = streamId,
            Payload = payloadBuffer
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    /// <summary>
    /// Send HEADERS frame
    /// </summary>
    private async Task SendHeadersAsync(int streamId, Dictionary<string, string> headers, bool endStream, CancellationToken cancellationToken)
    {
        // Encode headers using HPACK
        var headerBlock = _hpackEncoder.EncodeHeaders(headers.Select(h => (h.Key, h.Value)).ToList());
        
        byte flags = Http2Constants.FlagEndHeaders;
        if (endStream)
            flags |= Http2Constants.FlagEndStream;
        
        var frame = new Http2Frame
        {
            Length = headerBlock.Length,
            Type = Http2Constants.FrameTypeHeaders,
            Flags = flags,
            StreamId = streamId,
            Payload = headerBlock
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    /// <summary>
    /// Send DATA frame
    /// </summary>
    private async Task SendDataAsync(int streamId, ReadOnlyMemory<byte> data, bool endStream, CancellationToken cancellationToken)
    {
        byte flags = 0;
        if (endStream)
            flags = Http2Constants.FlagEndStream;
        
        // Convert ReadOnlyMemory to Memory for frame payload
        var payloadBuffer = new byte[data.Length];
        data.CopyTo(payloadBuffer);
        
        var frame = new Http2Frame
        {
            Length = data.Length,
            Type = Http2Constants.FrameTypeData,
            Flags = flags,
            StreamId = streamId,
            Payload = payloadBuffer
        };
        
        await SendFrameAsync(frame, cancellationToken);
    }
    
    private async Task SendFrameAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        var totalLen = Http2Constants.FrameHeaderLength + frame.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLen);
        frame.WriteHeader(buffer);
        if (frame.Length > 0)
            frame.Payload.Span.CopyTo(buffer.AsSpan(Http2Constants.FrameHeaderLength));

        // Queue and return — writer task owns the buffer lifecycle.
        await _writeChannel.Writer.WriteAsync(new PooledBuffer(buffer, totalLen, IsPooled: true), cancellationToken);
    }
    
    public ValueTask DisposeAsync()
    {
        _writeChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
