using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;

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
                        await SendCloseAsync(1000, null, cancellationToken);
                    return null;

                case ReceiveStep.ProtocolError:
                    _reader.AdvanceTo(consumed);
                    await SendCloseAsync(1002, "protocol error", cancellationToken);
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
        ProtocolError,
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

            if (header.IsControl)
            {
                // RFC 6455 §5.5: control frames MUST have payload ≤ 125 bytes and FIN=1.
                if (header.PayloadLength > 125 || !header.Fin)
                    return ReceiveStep.ProtocolError;

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
                        return ReceiveStep.CloseReceived;
                    // Pong: RFC 6455 §5.5.3 — ignore unsolicited pong
                }
                continue;
            }

            // Data frame: copy payload directly into reusable message buffer, then unmask.
            if (_firstFrame) { _messageOpcode = header.Opcode; _firstFrame = false; }

            var dest = _messageBuffer.GetSpan(header.PayloadLength);
            payloadSeq.CopyTo(dest);
            if (header.Masked) WebSocketFrame.ApplyMask(dest.Slice(0, header.PayloadLength), header.MaskKey);
            _messageBuffer.Advance(header.PayloadLength);

            if (header.Fin)
            {
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
