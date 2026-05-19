using System.Buffers;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;

namespace EffinitiveFramework.Core.Http3;

/// <summary>
/// HTTP/3 connection handler using QUIC transport (RFC 9114).
/// Each QUIC stream maps to an HTTP/3 request/response exchange.
/// Uses QPACK for header compression (RFC 9204).
/// </summary>
public sealed class Http3Connection : IAsyncDisposable
{
    private readonly QuicConnection _quicConnection;
    private readonly Func<HttpRequest, Task<HttpResponse>>? _requestHandler;
    private readonly QpackDecoder _qpackDecoder = new();
    private readonly QpackEncoder _qpackEncoder = new();
    private readonly SemaphoreSlim _maxConcurrentStreams;
    private readonly List<QuicStream> _uniStreams = new();

    // Critical streams tracked by type (like Kestrel)
    private QuicStream? _outboundControlStream;
    private QuicStream? _peerControlStream;
    private QuicStream? _peerEncoderStream;
    private QuicStream? _peerDecoderStream;
    private long _highestStreamId = -4; // Tracks highest bidi stream for GOAWAY

    // HTTP/3 frame types (RFC 9114 §7.2)
    private const long FrameTypeData = 0x00;
    private const long FrameTypeHeaders = 0x01;
    private const long FrameTypeSettings = 0x04;
    private const long FrameTypeGoaway = 0x07;

    // HTTP/3 unidirectional stream types (RFC 9114 §6.2)
    private const long StreamTypeControl = 0x00;
    private const long StreamTypeQpackEncoder = 0x02;
    private const long StreamTypeQpackDecoder = 0x03;

    // HTTP/3 settings (RFC 9114 §7.2.4.1)
    private const long SettingsMaxFieldSectionSize = 0x06;

    // HTTP/3 error codes
    private const long H3NoError = 0x0100;
    private const long H3InternalError = 0x0102;
    private const long H3ClosedCriticalStream = 0x0104;

    public Http3Connection(QuicConnection quicConnection, Func<HttpRequest, Task<HttpResponse>>? requestHandler = null)
    {
        _quicConnection = quicConnection;
        _requestHandler = requestHandler;
        _maxConcurrentStreams = new SemaphoreSlim(256, 256);
    }

    /// <summary>
    /// Process incoming HTTP/3 streams until the connection closes.
    /// </summary>
    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Open outbound control stream and send SETTINGS (RFC 9114 §6.2.1)
            _outboundControlStream = await _quicConnection.OpenOutboundStreamAsync(
                QuicStreamType.Unidirectional, cancellationToken);
            await SendSettingsAsync(_outboundControlStream, cancellationToken);

            // Accept all inbound streams
            while (!cancellationToken.IsCancellationRequested)
            {
                QuicStream stream;
                try
                {
                    stream = await _quicConnection.AcceptInboundStreamAsync(cancellationToken);
                }
                catch (QuicException)
                {
                    break;
                }

                if (stream.Type == QuicStreamType.Bidirectional)
                {
                    // Track highest stream ID for GOAWAY
                    var streamId = stream.Id;
                    if (streamId > _highestStreamId)
                        _highestStreamId = streamId;

                    _ = HandleStreamAsync(stream, cancellationToken);
                }
                else
                {
                    // Read stream type and classify (like Kestrel)
                    _ = ClassifyAndDrainUnidirectionalStreamAsync(stream, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (QuicException) { }
        finally
        {
            // Send GOAWAY for graceful shutdown (RFC 9114 §5.2)
            await SendGoAwayAsync(cancellationToken);

            // Clean up unidirectional streams — outbound control stream last
            // "Don't gracefully close the outbound control stream. If the peer
            // detects the control stream closes it will close with a protocol error."
            // — Kestrel comment. So dispose it only after everything else.
            List<QuicStream> streams;
            lock (_uniStreams) { streams = new(_uniStreams); _uniStreams.Clear(); }
            foreach (var s in streams)
            {
                try { await s.DisposeAsync(); } catch { }
            }

            if (_outboundControlStream != null)
            {
                try { await _outboundControlStream.DisposeAsync(); } catch { }
            }
        }
    }

    /// <summary>
    /// Read the stream type varint from an inbound unidirectional stream,
    /// classify it as control/encoder/decoder, and drain it for the connection lifetime.
    /// Kestrel does this to track critical streams by role.
    /// </summary>
    private async Task ClassifyAndDrainUnidirectionalStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        lock (_uniStreams) _uniStreams.Add(stream);

        try
        {
            // Read stream type (variable-length integer, RFC 9114 §6.2)
            var streamType = await ReadVariableIntAsync(stream, cancellationToken);

            // Classify the stream by type
            switch (streamType)
            {
                case StreamTypeControl:
                    _peerControlStream = stream;
                    break;
                case StreamTypeQpackEncoder:
                    _peerEncoderStream = stream;
                    break;
                case StreamTypeQpackDecoder:
                    _peerDecoderStream = stream;
                    break;
                // Unknown stream types: RFC 9114 §6.2 says "MUST be ignored"
            }

            // Drain remaining data — keep the stream alive for connection lifetime
            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
            }
        }
        catch (QuicException) { }
        catch (OperationCanceledException) { }
        // Do NOT dispose — disposed by ProcessAsync cleanup to avoid STOP_SENDING on critical streams
    }

    /// <summary>
    /// Send GOAWAY frame on the outbound control stream (RFC 9114 §5.2).
    /// Uses the highest opened stream ID + 4, like Kestrel.
    /// </summary>
    private async Task SendGoAwayAsync(CancellationToken cancellationToken)
    {
        if (_outboundControlStream == null) return;

        try
        {
            var goawayId = _highestStreamId + 4;
            if (goawayId < 0) goawayId = 0;

            var payload = new byte[8];
            var payloadLen = WriteVariableInt(payload.AsSpan(), goawayId);

            await WriteFrameAsync(_outboundControlStream, FrameTypeGoaway,
                payload.AsMemory(0, payloadLen), cancellationToken);
        }
        catch { }
    }

    private async Task HandleStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        await _maxConcurrentStreams.WaitAsync(cancellationToken);
        try
        {
            // Read frames from the stream
            var headers = await ReadHeadersAsync(stream, cancellationToken);
            if (headers == null)
                return;

            // Read body if present
            byte[] body = Array.Empty<byte>();
            if (!stream.ReadsClosed.IsCompleted)
            {
                body = await ReadBodyAsync(stream, cancellationToken);
            }

            // Convert to HTTP request
            var request = Http2RequestConverter.ConvertToHttp1Request(headers, body);

            // Process request
            HttpResponse response;
            if (_requestHandler != null)
            {
                response = await _requestHandler(request);
            }
            else
            {
                response = new HttpResponse
                {
                    StatusCode = 200,
                    Body = "HTTP/3 works!"u8.ToArray(),
                    ContentType = "text/plain"
                };
            }

            // Send response
            await SendResponseAsync(stream, response, cancellationToken);
        }
        catch (QuicException)
        {
            // Peer closed the connection/stream — don't send error codes on a dead stream.
            // This is the main cause of cascading H3_CLOSED_CRITICAL_STREAM errors.
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — no need to abort
        }
        catch (Exception)
        {
            try
            {
                stream.Abort(QuicAbortDirection.Write, H3InternalError);
            }
            catch { }
        }
        finally
        {
            _maxConcurrentStreams.Release();
            await stream.DisposeAsync();
        }
    }

    private async Task<List<(string name, string value)>?> ReadHeadersAsync(
        QuicStream stream, CancellationToken cancellationToken)
    {
        // Read frame type (variable-length integer)
        var frameType = await ReadVariableIntAsync(stream, cancellationToken);
        if (frameType != FrameTypeHeaders)
            return null;

        // Read frame length
        var frameLength = await ReadVariableIntAsync(stream, cancellationToken);
        if (frameLength <= 0 || frameLength > 65536)
            return null;

        // Read header block
        var headerBlock = ArrayPool<byte>.Shared.Rent((int)frameLength);
        try
        {
            await stream.ReadExactlyAsync(headerBlock.AsMemory(0, (int)frameLength), cancellationToken);
            return _qpackDecoder.Decode(headerBlock.AsSpan(0, (int)frameLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBlock);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (!stream.ReadsClosed.IsCompleted)
            {
                // Read frame type
                long frameType;
                try { frameType = await ReadVariableIntAsync(stream, cancellationToken); }
                catch { break; }

                var frameLength = await ReadVariableIntAsync(stream, cancellationToken);

                if (frameType == FrameTypeData && frameLength > 0)
                {
                    var remaining = (int)frameLength;
                    while (remaining > 0)
                    {
                        var toRead = Math.Min(remaining, buffer.Length);
                        var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                        if (read == 0) break;
                        ms.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                else
                {
                    // Skip non-DATA frames
                    var remaining = (int)frameLength;
                    while (remaining > 0)
                    {
                        var toRead = Math.Min(remaining, buffer.Length);
                        var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                        if (read == 0) break;
                        remaining -= read;
                    }
                    break; // After headers+data, we're done reading
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return ms.ToArray();
    }

    private async Task SendResponseAsync(QuicStream stream, HttpResponse response, CancellationToken cancellationToken)
    {
        response.MaterializeDeferredBody();

        // Build response headers for QPACK encoding
        var headers = new List<(string name, string value)>();
        headers.Add((":status", response.StatusCode.ToString()));
        if (!string.IsNullOrEmpty(response.ContentType))
            headers.Add(("content-type", response.ContentType));
        if (response.Body != null && response.Body.Length > 0)
            headers.Add(("content-length", response.Body.Length.ToString()));
        if (response.Headers != null)
        {
            foreach (var h in response.Headers)
                headers.Add((h.Key.ToLowerInvariant(), h.Value));
        }

        var encodedHeaders = _qpackEncoder.Encode(headers);

        // Write HEADERS frame
        await WriteFrameAsync(stream, FrameTypeHeaders, encodedHeaders, cancellationToken);

        // Write DATA frame if body present
        if (response.Body != null && response.Body.Length > 0)
        {
            await WriteFrameAsync(stream, FrameTypeData, response.Body, cancellationToken);
        }

        // Signal end of response
        stream.CompleteWrites();
    }

    private static async Task SendSettingsAsync(QuicStream controlStream, CancellationToken cancellationToken)
    {
        // Control stream type (0x00)
        await WriteVariableIntAsync(controlStream, 0x00, cancellationToken);

        // SETTINGS frame with max field section size
        var settingsPayload = new byte[16];
        var offset = 0;
        offset += WriteVariableInt(settingsPayload.AsSpan(offset), SettingsMaxFieldSectionSize);
        offset += WriteVariableInt(settingsPayload.AsSpan(offset), 65536);

        await WriteFrameAsync(controlStream, FrameTypeSettings, settingsPayload.AsMemory(0, offset), cancellationToken);
    }

    private static async Task WriteFrameAsync(QuicStream stream, long frameType, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        // Frame header: type (varint) + length (varint)
        Span<byte> header = stackalloc byte[16];
        var offset = WriteVariableInt(header, frameType);
        offset += WriteVariableInt(header.Slice(offset), payload.Length);

        await stream.WriteAsync(header.Slice(0, offset).ToArray(), cancellationToken);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task WriteVariableIntAsync(QuicStream stream, long value, CancellationToken cancellationToken)
    {
        Span<byte> buf = stackalloc byte[8];
        var len = WriteVariableInt(buf, value);
        await stream.WriteAsync(buf.Slice(0, len).ToArray(), cancellationToken);
    }

    /// <summary>
    /// Encode a QUIC variable-length integer (RFC 9000 §16).
    /// </summary>
    private static int WriteVariableInt(Span<byte> buffer, long value)
    {
        if (value < 0x40)
        {
            buffer[0] = (byte)value;
            return 1;
        }
        if (value < 0x4000)
        {
            buffer[0] = (byte)(0x40 | (value >> 8));
            buffer[1] = (byte)value;
            return 2;
        }
        if (value < 0x40000000)
        {
            buffer[0] = (byte)(0x80 | (value >> 24));
            buffer[1] = (byte)(value >> 16);
            buffer[2] = (byte)(value >> 8);
            buffer[3] = (byte)value;
            return 4;
        }
        buffer[0] = (byte)(0xC0 | (value >> 56));
        buffer[1] = (byte)(value >> 48);
        buffer[2] = (byte)(value >> 40);
        buffer[3] = (byte)(value >> 32);
        buffer[4] = (byte)(value >> 24);
        buffer[5] = (byte)(value >> 16);
        buffer[6] = (byte)(value >> 8);
        buffer[7] = (byte)value;
        return 8;
    }

    /// <summary>
    /// Read a QUIC variable-length integer from a stream.
    /// </summary>
    private static async Task<long> ReadVariableIntAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var firstByte = new byte[1];
        await stream.ReadExactlyAsync(firstByte, cancellationToken);

        var prefix = firstByte[0] >> 6;
        long value = firstByte[0] & 0x3F;

        var length = 1 << prefix;
        if (length > 1)
        {
            var remaining = new byte[length - 1];
            await stream.ReadExactlyAsync(remaining, cancellationToken);
            for (int i = 0; i < remaining.Length; i++)
                value = (value << 8) | remaining[i];
        }

        return value;
    }

    public async ValueTask DisposeAsync()
    {
        _maxConcurrentStreams.Dispose();
        await _quicConnection.DisposeAsync();
    }
}
