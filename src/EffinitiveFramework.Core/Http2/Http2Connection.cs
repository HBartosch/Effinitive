using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2.Hpack;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Manages an HTTP/2 connection
/// </summary>
public class Http2Connection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ConcurrentDictionary<int, Http2Stream> _streams = new();
    private readonly ConcurrentDictionary<int, Http2Stream> _pushedStreams = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StreamPriorityScheduler _priorityScheduler = new();
    private HpackDecoder _hpackDecoder;
    private readonly HpackEncoder _hpackEncoder = new();
    private readonly Func<HttpRequest, Task<HttpResponse>>? _requestHandler;
    
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
        var pipe = new Pipe();
        _reader = pipe.Reader;
        _writer = pipe.Writer;
    }
    
    /// <summary>
    /// Start processing the HTTP/2 connection
    /// </summary>
    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Wait for client preface
            if (!await ReceiveClientPrefaceAsync(cancellationToken))
            {
                await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                return;
            }
            
            // Send initial SETTINGS frame
            await SendSettingsAsync(cancellationToken);
            
            // Process frames
            await ProcessFramesAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Log error and send GOAWAY
            await SendGoAwayAsync(Http2Constants.ErrorInternalError, cancellationToken);
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
            
            if (responseBody.Length > _connectionWindowSize)
                throw new InvalidOperationException($"Pushed data ({responseBody.Length} bytes) exceeds connection flow control window ({_connectionWindowSize} bytes)");
            
            // Update flow control windows
            pushedStream.UpdateWindowSize(-responseBody.Length);
            _connectionWindowSize -= responseBody.Length;
            
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
        
        var bytesRead = await _stream.ReadAsync(buffer, 0, prefaceLength, cancellationToken);
        
        if (bytesRead != prefaceLength)
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
        var settings = new (ushort id, uint value)[]
        {
            (Http2Constants.SettingsHeaderTableSize, _headerTableSize),
            (Http2Constants.SettingsEnablePush, _enablePush),
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
        
        // Write settings payload
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
        
        await _stream.WriteAsync(frameBuffer, 0, frameBuffer.Length, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }
    
    /// <summary>
    /// Process incoming frames
    /// </summary>
    private async Task ProcessFramesAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[Http2Constants.FrameHeaderLength];
        
        while (!cancellationToken.IsCancellationRequested)
        {
            // Read frame header
            var bytesRead = await _stream.ReadAsync(headerBuffer, 0, Http2Constants.FrameHeaderLength, cancellationToken);
            
            if (bytesRead == 0)
            {
                break; // Connection closed
            }
            
            if (bytesRead != Http2Constants.FrameHeaderLength)
            {
                await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                break;
            }
            
            if (!Http2Frame.TryParseHeader(headerBuffer, out var frame))
            {
                await SendGoAwayAsync(Http2Constants.ErrorProtocolError, cancellationToken);
                break;
            }
            
            // Read frame payload if present
            if (frame.Length > 0)
            {
                // SECURITY: Validate frame size doesn't exceed max (prevents DoS)
                if (frame.Length > _maxFrameSize)
                {
                    await SendGoAwayAsync(Http2Constants.ErrorFrameSizeError, cancellationToken);
                    break;
                }
                
                var payloadBuffer = ArrayPool<byte>.Shared.Rent(frame.Length);
                try
                {
                    bytesRead = await _stream.ReadAsync(payloadBuffer, 0, frame.Length, cancellationToken);
                    if (bytesRead != frame.Length)
                    {
                        await SendGoAwayAsync(Http2Constants.ErrorFrameSizeError, cancellationToken);
                        break;
                    }
                    
                    frame.Payload = payloadBuffer.AsMemory(0, frame.Length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                }
            }
            
            // Process frame based on type
            await ProcessFrameAsync(frame, cancellationToken);
        }
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
            
            case Http2Constants.FrameTypeGoAway:
                // Connection closing
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
        
        // Decode HPACK headers
        List<(string name, string value)> headers;
        try
        {
            var headerBlock = frame.Payload.Slice(payloadOffset);
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
        
        // If END_HEADERS flag is set, process the request
        if (frame.HasFlag(Http2Constants.FlagEndHeaders))
        {
            if (frame.HasFlag(Http2Constants.FlagEndStream))
            {
                // No body expected, process immediately
                stream.UpdateState(Http2StreamState.HalfClosedRemote);
                await ProcessStreamRequestAsync(stream, cancellationToken);
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
            
            // Update flow control (send WINDOW_UPDATE)
            // RFC 7540: Do NOT send WINDOW_UPDATE with increment=0
            await SendWindowUpdateAsync(streamId, dataLength, cancellationToken);
            await SendWindowUpdateAsync(0, dataLength, cancellationToken); // Connection level
        }
        
        // If END_STREAM, process the request
        if (frame.HasFlag(Http2Constants.FlagEndStream))
        {
            stream.UpdateState(Http2StreamState.HalfClosedRemote);
            await ProcessStreamRequestAsync(stream, cancellationToken);
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
            
            // Close stream and remove from dictionary
            stream.UpdateState(Http2StreamState.Closed);
            _streams.TryRemove(stream.StreamId, out _);
        }
        catch (Exception)
        {
            await SendRstStreamAsync(stream.StreamId, Http2Constants.ErrorInternalError, cancellationToken);
            
            // Remove failed stream from dictionary
            _streams.TryRemove(stream.StreamId, out _);
        }
    }
    
    private async Task SendResponseAsync(int streamId, HttpResponse response, CancellationToken cancellationToken)
    {
        // Convert response to HTTP/2 headers
        var headers = Http2ResponseConverter.ConvertToHttp2Headers(response);
        
        // Encode headers with HPACK
        var encodedHeaders = _hpackEncoder.EncodeHeaders(headers);
        
        // Send HEADERS frame
        var headersFrame = new Http2Frame
        {
            Length = encodedHeaders.Length,
            Type = Http2Constants.FrameTypeHeaders,
            Flags = Http2Constants.FlagEndHeaders,
            StreamId = streamId,
            Payload = encodedHeaders
        };
        
        if (response.Body == null || response.Body.Length == 0)
        {
            // No body, set END_STREAM on HEADERS
            headersFrame.SetFlag(Http2Constants.FlagEndStream);
            await SendFrameAsync(headersFrame, cancellationToken);
        }
        else
        {
            // Send HEADERS without END_STREAM
            await SendFrameAsync(headersFrame, cancellationToken);
            
            // Send DATA frame with body
            var dataFrame = new Http2Frame
            {
                Length = response.Body?.Length ?? 0,
                Type = Http2Constants.FrameTypeData,
                Flags = Http2Constants.FlagEndStream,
                StreamId = streamId,
                Payload = response.Body ?? Array.Empty<byte>()
            };
            
            await SendFrameAsync(dataFrame, cancellationToken);
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
            _connectionWindowSize += increment;
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
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var buffer = new byte[Http2Constants.FrameHeaderLength + frame.Length];
            frame.WriteHeader(buffer);
            
            if (frame.Length > 0)
                frame.Payload.Span.CopyTo(buffer.AsSpan(Http2Constants.FrameHeaderLength));
            
            await _stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        await _reader.CompleteAsync();
        await _writer.CompleteAsync();
        _writeLock.Dispose();
    }
}
