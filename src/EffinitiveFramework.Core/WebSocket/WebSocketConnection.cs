using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace EffinitiveFramework.Core.WebSocket;

/// <summary>
/// Represents a WebSocket connection that has been upgraded from HTTP/1.1.
/// Implements RFC 6455 frame read/write, ping/pong, and close handshake.
/// Zero per-message allocations: _messageBuffer is reused across ReceiveAsync calls,
/// and frame payloads are copied directly into it without an intermediate byte[].
/// </summary>
public sealed class WebSocketConnection : IAsyncDisposable
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    // True when this instance created the pipes and must therefore complete
    // them. False when they belong to the connection, which completes them
    // itself; completing another owner's pipes would tear down its transport.
    private readonly bool _ownsPipes;
    private readonly ArrayBufferWriter<byte> _messageBuffer;
    // RFC 6455 §5.5: control frames carry at most 125 bytes, so this is sized
    // by the specification and never needs to grow.
    private readonly byte[] _controlScratch = new byte[125];

    // Message assembly state. Fields rather than locals in ReceiveAsync: a
    // local that is live across an await becomes a field on the compiler's
    // state machine, which is boxed on every suspension.
    private WebSocketOpcode _messageOpcode;
    private bool _firstFrame;
    private bool _pongPending;
    private bool _closeSent;
    private bool _closeReceived;
    // Set by ReceiveAsync when more frames remain in the pipe buffer after returning a message.
    // SendAsync uses this to defer FlushAsync, batching multiple responses into one syscall.
    private bool _hasPendingData;

    /// <summary>
    /// Whether the WebSocket connection is still open.
    /// </summary>
    public bool IsOpen => !_closeSent && !_closeReceived;

    /// <summary>
    /// Wrap a stream, for connections that have one: TLS terminates through an
    /// <see cref="System.Net.Security.SslStream"/> and there is no pipe to take.
    /// </summary>
    internal WebSocketConnection(Stream stream)
    {
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(minimumBufferSize: 65536, leaveOpen: true));
        _messageBuffer = new ArrayBufferWriter<byte>(65536);
        _ownsPipes = true;
    }

    /// <summary>
    /// Take the connection's own pipes, for a cleartext connection that already
    /// has them.
    /// </summary>
    /// <remarks>
    /// The alternative is to adapt those pipes to a <see cref="Stream"/> and
    /// build a second pipe over it, which costs a copy in each direction and an
    /// extra flush per message: the stream adapter flushes on write, and the
    /// pipe above it flushes the adapter afterwards. On an echo workload, where
    /// every message is a whole read and a whole write, that overhead is the
    /// per-message cost rather than a rounding error.
    /// </remarks>
    internal WebSocketConnection(PipeReader reader, PipeWriter writer)
    {
        _reader = reader;
        _writer = writer;
        _messageBuffer = new ArrayBufferWriter<byte>(65536);
        _ownsPipes = false;
    }

    /// <summary>
    /// Compute the Sec-WebSocket-Accept value per RFC 6455 §4.2.2.
    /// </summary>
    public static string ComputeAcceptKey(string clientKey)
    {
        var combined = clientKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Read the next message from the client.
    /// Handles fragmentation (continuation frames), ping/pong automatically.
    /// Returns null when the connection is closed.
    /// </summary>
    public async ValueTask<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        _messageBuffer.ResetWrittenCount(); // Reuse existing allocation — no heap object per call.
        _messageOpcode = default;
        _firstFrame = true;

        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);

            var step = ParseFrames(result.Buffer, out var consumed, out var examined);

            switch (step)
            {
                case ReceiveStep.Message:
                    _reader.AdvanceTo(consumed);
                    await FlushPendingPongAsync(cancellationToken);
                    return new WebSocketMessage(
                        _messageOpcode == WebSocketOpcode.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                        _messageBuffer.WrittenMemory);

                case ReceiveStep.CloseReceived:
                    _closeReceived = true;
                    _reader.AdvanceTo(consumed);
                    if (!_closeSent)
                        await SendCloseAsync(_peerCloseCode, null, cancellationToken);
                    return null;

                case ReceiveStep.Fail:
                    _reader.AdvanceTo(consumed);
                    await SendCloseAsync(_failCode, _failReason, cancellationToken);
                    return null;

                default:
                    // AdvanceTo(consumed, examined) tells the pipe to wait for data past
                    // examined; AdvanceTo(consumed) alone would return immediately.
                    _reader.AdvanceTo(consumed, examined);
                    await FlushPendingPongAsync(cancellationToken);
                    if (result.IsCompleted) return null;
                    break;
            }
        }
    }

    /// <summary>
    /// Check a close frame's payload and decide what to echo back.
    /// </summary>
    /// <remarks>
    /// RFC 6455 §5.5.1 allows a close frame to carry nothing, or a two-octet status code
    /// optionally followed by a reason. One octet is neither, so there is no code to read and
    /// the frame is malformed. The reason is text, so §5.5.1 requires it to be valid UTF-8.
    ///
    /// §7.4.1 reserves the codes. 1005 and 1006 are for an application to observe locally and
    /// "MUST NOT be set as a status code in a Close control frame by an endpoint", 1004 has no
    /// meaning, and the rest of 1000-2999 is unassigned; 3000-4999 is open for libraries and
    /// applications. A code outside that leaves the peer's intent unreadable.
    /// </remarks>
    private ReceiveStep ValidateClose(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            _peerCloseCode = 1000;
            return ReceiveStep.CloseReceived;
        }

        if (payload.Length == 1)
            return Fail(1002, "close payload of one octet carries no status code");

        ushort code = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (!IsSendableCloseCode(code))
            return Fail(1002, "reserved close status code");

        if (!Utf8.IsValid(payload[2..]))
            return Fail(1007, "invalid UTF-8 in close reason");

        _peerCloseCode = code;
        return ReceiveStep.CloseReceived;
    }

    /// <summary>Whether RFC 6455 §7.4.1 allows this code to appear in a close frame.</summary>
    private static bool IsSendableCloseCode(ushort code) => code switch
    {
        // 1004 was never defined; 1005 and 1006 are observed locally, never sent.
        1000 or 1001 or 1002 or 1003 or 1007 or 1008 or 1009 or 1010 or 1011 => true,
        // Registered after RFC 6455: 1012 Service Restart, 1013 Try Again Later, 1014 Bad Gateway.
        1012 or 1013 or 1014 => true,
        _ => code >= 3000 && code <= 4999,
    };

    /// <summary>Whether RFC 6455 §5.2 gives this opcode a meaning.</summary>
    private static bool IsDefinedOpcode(WebSocketOpcode opcode) => opcode is
        WebSocketOpcode.Continuation or WebSocketOpcode.Text or WebSocketOpcode.Binary or
        WebSocketOpcode.Close or WebSocketOpcode.Ping or WebSocketOpcode.Pong;

    private ValueTask FlushPendingPongAsync(CancellationToken cancellationToken)
    {
        if (!_pongPending) return ValueTask.CompletedTask;
        _pongPending = false;
        return new ValueTask(_writer.FlushAsync(cancellationToken).AsTask());
    }

    private enum ReceiveStep : byte
    {
        NeedMoreData,
        Message,
        CloseReceived,
        Fail,
    }

    // Close code for the pending failure. RFC 6455 §7.4.1: 1002 for a protocol error, 1007 for
    // payload that is not consistent with the message type.
    private ushort _failCode;
    private string? _failReason;

    // Echoed back when the peer closes. RFC 6455 §5.5.1: the reply should carry the same code.
    private ushort _peerCloseCode = 1000;

    private ReceiveStep Fail(ushort code, string reason)
    {
        _failCode = code;
        _failReason = reason;
        return ReceiveStep.Fail;
    }

    /// <summary>
    /// Consume whole frames from <paramref name="buffer"/> until a message is
    /// complete or the data runs out, and report what the caller must do next.
    /// </summary>
    /// <remarks>
    /// Deliberately not async. Every local here is live only within one call,
    /// so none of them, and none of the multi-word structs among them, ends up
    /// on the state machine that ReceiveAsync boxes when it suspends. Anything
    /// needing an await is reported back rather than performed: a pong is
    /// written but left unflushed, and close is signalled rather than sent.
    /// </remarks>
    private ReceiveStep ParseFrames(
        ReadOnlySequence<byte> buffer,
        out SequencePosition consumed,
        out SequencePosition examined)
    {
        consumed = buffer.Start;
        examined = buffer.End;

        while (WebSocketFrame.TryParseHeader(buffer, out var header, out var headerConsumed))
        {
            var afterHeader = buffer.Slice(headerConsumed);
            if (afterHeader.Length < header.PayloadLength) break; // wait for rest of payload

            var payloadSeq = afterHeader.Slice(0, header.PayloadLength);
            consumed = afterHeader.GetPosition(header.PayloadLength);
            buffer = buffer.Slice(consumed);

            // RFC 6455 §5.2: RSV1-3 must be zero unless an extension that defines them has been
            // negotiated. None is, so any of them set is a frame this endpoint cannot interpret.
            if (header.Rsv != 0)
                return Fail(1002, "reserved bit set");

            // RFC 6455 §5.2 lists every opcode; 0x3-0x7 and 0xB-0xF are reserved, and a frame
            // carrying one cannot be acted on without guessing what it means.
            if (!IsDefinedOpcode(header.Opcode))
                return Fail(1002, "reserved opcode");

            if (header.IsControl)
            {
                // RFC 6455 §5.5: control frames MUST have payload ≤ 125 bytes and FIN=1.
                if (header.PayloadLength > 125 || !header.Fin)
                    return Fail(1002, "malformed control frame");

                // Reused per connection rather than stack-allocated per frame:
                // one read can carry many control frames, and a stackalloc
                // inside the loop grows the frame by up to 125 bytes for each
                // of them (CA2014). The length is bounded by the check above.
                var ctrlPayload = _controlScratch.AsSpan(0, header.PayloadLength);
                payloadSeq.CopyTo(ctrlPayload);
                if (header.Masked) WebSocketFrame.ApplyMask(ctrlPayload, header.MaskKey);

                switch (header.Opcode)
                {
                    case WebSocketOpcode.Ping:
                        WriteFrameHeader(_writer, WebSocketOpcode.Pong, ctrlPayload.Length);
                        _writer.Write(ctrlPayload);
                        _pongPending = true; // flushed by the caller, which can await
                        break;
                    case WebSocketOpcode.Close:
                        return ValidateClose(ctrlPayload);
                    // Pong: RFC 6455 §5.5.3 — ignore unsolicited pong
                }
                continue;
            }

            // RFC 6455 §5.4: a continuation only continues something. A data frame arriving while
            // a message is already in progress, or a continuation arriving when none is, leaves
            // the stream with no coherent reading.
            if (_firstFrame && header.Opcode == WebSocketOpcode.Continuation)
                return Fail(1002, "continuation with nothing to continue");
            if (!_firstFrame && header.Opcode != WebSocketOpcode.Continuation)
                return Fail(1002, "new data frame inside a fragmented message");

            // Data frame: copy payload directly into reusable message buffer, then unmask.
            if (_firstFrame) { _messageOpcode = header.Opcode; _firstFrame = false; }

            var dest = _messageBuffer.GetSpan(header.PayloadLength);
            payloadSeq.CopyTo(dest);
            if (header.Masked) WebSocketFrame.ApplyMask(dest.Slice(0, header.PayloadLength), header.MaskKey);
            _messageBuffer.Advance(header.PayloadLength);

            if (header.Fin)
            {
                // RFC 6455 §5.6: a text message's payload MUST be valid UTF-8, and §8.1 requires
                // the connection to fail when it is not. Checked on the whole message rather than
                // per frame, because a fragment may legitimately end mid-codepoint (§5.4).
                if (_messageOpcode == WebSocketOpcode.Text &&
                    !Utf8.IsValid(_messageBuffer.WrittenSpan))
                    return Fail(1007, "invalid UTF-8 in text message");

                // Defer the flush only when another WHOLE frame is already
                // buffered, so the response about to be written is certain to be
                // followed by another without waiting on the network.
                //
                // RFC 6455 §5.2 frames carry a length, so a frame is only
                // actionable once that many payload bytes have arrived; a
                // complete frame trailed by one byte of the next offers nothing
                // to process, and deferring on it holds an answer the client has
                // already earned.
                _hasPendingData = HasCompleteFrame(buffer);
                examined = consumed;
                return ReceiveStep.Message;
            }
        }

        return ReceiveStep.NeedMoreData;
    }

    /// <summary>
    /// Send a message to the client.
    /// When more client frames are already buffered (_hasPendingData), defers FlushAsync so that
    /// a batch of responses can be written and flushed in a single syscall.
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken = default)
    {
        var opcode = type == WebSocketMessageType.Text ? WebSocketOpcode.Text : WebSocketOpcode.Binary;
        WriteFrameHeader(_writer, opcode, data.Length);
        _writer.Write(data.Span);
        if (!_hasPendingData)
            await _writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Send a close frame and close the connection.
    /// </summary>
    public async ValueTask SendCloseAsync(ushort statusCode, string? reason, CancellationToken cancellationToken = default)
    {
        if (_closeSent) return;
        _closeSent = true;

        var reasonBytes = reason != null ? Encoding.UTF8.GetBytes(reason) : Array.Empty<byte>();
        int payloadLength = 2 + reasonBytes.Length;

        WriteFrameHeader(_writer, WebSocketOpcode.Close, payloadLength);
        var closeSpan = _writer.GetSpan(payloadLength);
        closeSpan[0] = (byte)(statusCode >> 8);
        closeSpan[1] = (byte)statusCode;
        if (reasonBytes.Length > 0) reasonBytes.CopyTo(closeSpan.Slice(2));
        _writer.Advance(payloadLength);

        await _writer.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closeSent)
        {
            try { await SendCloseAsync(1000, null, CancellationToken.None); }
            catch { /* best effort */ }
        }

        // Only complete pipes this instance created. When they belong to the
        // connection, it completes them during its own teardown.
        if (_ownsPipes)
        {
            await _reader.CompleteAsync();
            await _writer.CompleteAsync();
        }
    }

    /// <summary>
    /// Whether <paramref name="buffer"/> already holds a frame in full: a
    /// parseable header and the whole payload it declares.
    /// </summary>
    private static bool HasCompleteFrame(ReadOnlySequence<byte> buffer)
        => WebSocketFrame.TryParseHeader(buffer, out var header, out var headerConsumed)
           && buffer.Slice(headerConsumed).Length >= header.PayloadLength;

    /// <summary>
    /// Write a WebSocket frame header directly to the PipeWriter. No intermediate buffer.
    /// </summary>
    private static void WriteFrameHeader(PipeWriter writer, WebSocketOpcode opcode, int payloadLength)
    {
        int headerSize = payloadLength < 126 ? 2 : payloadLength <= 65535 ? 4 : 10;
        var span = writer.GetSpan(headerSize);
        span[0] = (byte)(0x80 | (byte)opcode); // FIN + opcode
        if (payloadLength < 126)
        {
            span[1] = (byte)payloadLength;
        }
        else if (payloadLength <= 65535)
        {
            span[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2), (ushort)payloadLength);
        }
        else
        {
            span[1] = 127;
            BinaryPrimitives.WriteInt64BigEndian(span.Slice(2), (long)payloadLength);
        }
        writer.Advance(headerSize);
    }
}

/// <summary>
/// WebSocket message types.
/// </summary>
public enum WebSocketMessageType
{
    Text,
    Binary
}

/// <summary>
/// A complete WebSocket message (may have been reassembled from multiple frames).
/// Data points into WebSocketConnection._messageBuffer — valid until the next ReceiveAsync call.
/// </summary>
public readonly struct WebSocketMessage
{
    public readonly WebSocketMessageType Type;
    public readonly ReadOnlyMemory<byte> Data;

    public WebSocketMessage(WebSocketMessageType type, ReadOnlyMemory<byte> data)
    {
        Type = type;
        Data = data;
    }

    /// <summary>
    /// Get the message data as a UTF-8 string.
    /// </summary>
    public string GetText() => Encoding.UTF8.GetString(Data.Span);
}
