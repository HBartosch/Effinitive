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

    // Peer address for the whole QUIC connection — stamped onto every request built from its streams.
    // The string form is cached alongside it so per-request stamping never allocates.
    private readonly System.Net.IPAddress? _remoteIpAddress;
    private readonly string? _remoteIpText;

    public Http3Connection(QuicConnection quicConnection, Func<HttpRequest, Task<HttpResponse>>? requestHandler = null)
    {
        _quicConnection = quicConnection;
        _requestHandler = requestHandler;

        // Unmap IPv4-mapped IPv6 addresses so a client reaches the same rate-limit partition whether it
        // arrives over HTTP/3 or HTTP/1.1.
        if (quicConnection.RemoteEndPoint is System.Net.IPEndPoint remote)
        {
            _remoteIpAddress = remote.Address.IsIPv4MappedToIPv6
                ? remote.Address.MapToIPv4()
                : remote.Address;
            _remoteIpText = _remoteIpAddress.ToString();
        }
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
        try
        {
            // One buffered reader for the whole stream, so the frame headers and the field
            // section come out of a single read rather than one read per field.
            using var reader = new Http3FrameReader(stream);

            var headers = await ReadHeadersAsync(reader, cancellationToken);
            if (headers == null)
                return;

            // Read body if present
            byte[] body = Array.Empty<byte>();
            if (reader.HasBuffered || !stream.ReadsClosed.IsCompleted)
            {
                body = await ReadBodyAsync(reader, cancellationToken);
            }

            // Convert to HTTP request
            var request = Http2RequestConverter.ConvertToHttp1Request(
                headers, body, _remoteIpAddress, Http.HttpVersions.Http30);
            request.RemoteIpAddressText = _remoteIpText;

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
            await stream.DisposeAsync();
        }
    }

    private async Task<List<(string name, string value)>?> ReadHeadersAsync(
        Http3FrameReader reader, CancellationToken cancellationToken)
    {
        // Read frame type (variable-length integer)
        var frameType = await reader.ReadVariableIntAsync(cancellationToken);
        if (frameType != FrameTypeHeaders)
            return null;

        // Read frame length
        var frameLength = await reader.ReadVariableIntAsync(cancellationToken);
        if (frameLength <= 0 || frameLength > 65536)
            return null;

        // Decoded straight out of the buffer: the field section is almost always already there,
        // having arrived in the same read as the frame header.
        if (!await reader.EnsureAsync((int)frameLength, cancellationToken))
            return null;

        try
        {
            return _qpackDecoder.Decode(reader.Peek((int)frameLength));
        }
        finally
        {
            reader.Advance((int)frameLength);
        }
    }

    /// <summary>
    /// Reads the request content, which arrives as DATA frames (RFC 9114 §7.2.1).
    /// </summary>
    /// <remarks>
    /// Takes the same buffered reader the field section came from, so content that arrived in
    /// that first read is already in hand and costs no further wait.
    /// </remarks>
    private static async Task<byte[]> ReadBodyAsync(Http3FrameReader reader, CancellationToken cancellationToken)
    {
        MemoryStream? content = null;

        while (true)
        {
            long frameType;
            try { frameType = await reader.ReadVariableIntAsync(cancellationToken); }
            catch { break; }

            if (frameType < 0)
                break;

            var frameLength = await reader.ReadVariableIntAsync(cancellationToken);
            if (frameLength < 0)
                break;

            if (frameLength > 0)
            {
                if (!await reader.EnsureAsync((int)frameLength, cancellationToken))
                    break;

                if (frameType == FrameTypeData)
                {
                    content ??= new MemoryStream((int)frameLength);
                    content.Write(reader.Peek((int)frameLength));
                }

                // Frame types that are not DATA are skipped rather than rejected: RFC 9114 §9
                // reserves unknown frame types for extension and requires a receiver to ignore them.
                reader.Advance((int)frameLength);
            }
        }

        return content == null ? Array.Empty<byte>() : content.ToArray();
    }

    private async Task SendResponseAsync(QuicStream stream, HttpResponse response, CancellationToken cancellationToken)
    {
        response.MaterializeDeferredBody();

        // The HTTP/3 framing path delivers the body from a byte[]. A stream-backed body
        // (e.g. a static file) is read into memory here, bounded by BodyStreamLength.
        await response.MaterializeBodyStreamAsync(cancellationToken);

        var body = response.Body;
        var bodyLength = body?.Length ?? 0;

        // Single WriteAsync per response: HEADERS frame + optional DATA frame batched into one buffer.
        // Previously 4 separate WriteAsync calls; each QUIC stream write acquires the send lock.
        var buf = BuildResponseBuffer(response, body, bodyLength, out var totalSize);
        try
        {
            // The end-of-stream flag rides with the data rather than following it. Both say the
            // same thing to the peer, but CompleteWrites is a second call into the transport, a
            // StreamShutdown, where this overload sets QUIC_SEND_FLAGS.FIN on the send already
            // being made. One response, one trip through the connection's send path.
            await stream.WriteAsync(buf.AsMemory(0, totalSize), completeWrites: true, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Encodes the field section and frames the whole response into one rented buffer.
    /// </summary>
    /// <remarks>
    /// The field section is encoded into stack space rather than a pooled array. Renting one per
    /// response was measurably slower than the allocation it replaced: a field section is a few
    /// dozen bytes and dies immediately, which is what gen0 is for, whereas the pool is shared and
    /// at a few hundred thousand responses a second the traffic through it costs more than the
    /// garbage did. Measured across five alternating runs it was about 4% down. Non-async so the
    /// stackalloc is valid, since there is no await boundary here.
    /// </remarks>
    private static byte[] BuildResponseBuffer(HttpResponse response, byte[]? body, int bodyLength, out int totalSize)
    {
        Span<byte> headerScratch = stackalloc byte[1024];
        byte[]? rentedHeaders = null;
        var encodedHeaders = headerScratch;

        int headerLength;
        while (!QpackEncoder.TryEncodeResponse(response, bodyLength, encodedHeaders, out headerLength))
        {
            // Only for a response carrying unusually many or unusually long fields.
            var size = (rentedHeaders?.Length ?? headerScratch.Length) * 2;
            if (rentedHeaders != null) ArrayPool<byte>.Shared.Return(rentedHeaders);
            rentedHeaders = ArrayPool<byte>.Shared.Rent(size);
            encodedHeaders = rentedHeaders;
        }

        encodedHeaders = encodedHeaders[..headerLength];

        Span<byte> hdrPrefix = stackalloc byte[16];
        int hp = WriteVariableInt(hdrPrefix, FrameTypeHeaders);
        hp += WriteVariableInt(hdrPrefix.Slice(hp), encodedHeaders.Length);

        totalSize = hp + encodedHeaders.Length;

        Span<byte> dataPrefix = stackalloc byte[16];
        int dp = 0;
        if (bodyLength > 0)
        {
            dp = WriteVariableInt(dataPrefix, FrameTypeData);
            dp += WriteVariableInt(dataPrefix.Slice(dp), bodyLength);
            totalSize += dp + bodyLength;
        }

        var buf = ArrayPool<byte>.Shared.Rent(totalSize);
        int pos = 0;
        hdrPrefix.Slice(0, hp).CopyTo(buf.AsSpan(pos)); pos += hp;
        encodedHeaders.CopyTo(buf.AsSpan(pos)); pos += encodedHeaders.Length;
        if (bodyLength > 0)
        {
            dataPrefix.Slice(0, dp).CopyTo(buf.AsSpan(pos)); pos += dp;
            body!.CopyTo(buf.AsSpan(pos));
        }

        if (rentedHeaders != null)
            ArrayPool<byte>.Shared.Return(rentedHeaders);

        return buf;
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
        // Use a pooled 8-byte buffer to avoid per-call heap allocation (new byte[1] / new byte[N]).
        var buf = ArrayPool<byte>.Shared.Rent(8);
        try
        {
            await stream.ReadExactlyAsync(buf.AsMemory(0, 1), cancellationToken);
            var prefix = buf[0] >> 6;
            long value = buf[0] & 0x3F;

            int extra = (1 << prefix) - 1;
            if (extra > 0)
            {
                await stream.ReadExactlyAsync(buf.AsMemory(0, extra), cancellationToken);
                for (int i = 0; i < extra; i++)
                    value = (value << 8) | buf[i];
            }

            return value;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _quicConnection.DisposeAsync();
    }
}
