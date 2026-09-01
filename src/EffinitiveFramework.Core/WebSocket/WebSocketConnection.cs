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
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ArrayBufferWriter<byte> _messageBuffer;
    private bool _closeSent;
    private bool _closeReceived;
    // Set by ReceiveAsync when more frames remain in the pipe buffer after returning a message.
    // SendAsync uses this to defer FlushAsync, batching multiple responses into one syscall.
    private bool _hasPendingData;

    /// <summary>
    /// Whether the WebSocket connection is still open.
    /// </summary>
    public bool IsOpen => !_closeSent && !_closeReceived;

    internal WebSocketConnection(Stream stream)
    {
        _stream = stream;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 65536, leaveOpen: true));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(minimumBufferSize: 65536, leaveOpen: true));
        _messageBuffer = new ArrayBufferWriter<byte>(65536);
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
        WebSocketOpcode messageOpcode = default;
        bool firstFrame = true;
        bool gotMessage = false;

        while (!gotMessage)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);

            var buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;

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
                    {
                        _reader.AdvanceTo(consumed);
                        await SendCloseAsync(1002, "protocol error", cancellationToken);
                        return null;
                    }
                    Span<byte> ctrlPayload = stackalloc byte[header.PayloadLength];
                    payloadSeq.CopyTo(ctrlPayload);
                    if (header.Masked) WebSocketFrame.ApplyMask(ctrlPayload, header.MaskKey);

                    switch (header.Opcode)
                    {
                        case WebSocketOpcode.Ping:
                            WriteFrameHeader(_writer, WebSocketOpcode.Pong, ctrlPayload.Length);
                            _writer.Write(ctrlPayload); // sync — no await, span safe before FlushAsync
                            await _writer.FlushAsync(cancellationToken);
                            break;
                        case WebSocketOpcode.Close:
                            _closeReceived = true;
                            _reader.AdvanceTo(consumed);
                            if (!_closeSent)
                                await SendCloseAsync(1000, null, cancellationToken);
                            return null;
                        // Pong: RFC 6455 §5.5.3 — ignore unsolicited pong
                    }
                    continue;
                }

                // Data frame: copy payload directly into reusable message buffer, then unmask.
                if (firstFrame) { messageOpcode = header.Opcode; firstFrame = false; }

                var dest = _messageBuffer.GetSpan(header.PayloadLength);
                payloadSeq.CopyTo(dest);
                if (header.Masked) WebSocketFrame.ApplyMask(dest.Slice(0, header.PayloadLength), header.MaskKey);
                _messageBuffer.Advance(header.PayloadLength);

                if (header.Fin)
                {
                    // Defer the flush only when another WHOLE frame is already
                    // buffered, so the response about to be written is certain
                    // to be followed by another without waiting on the network.
                    //
                    // "Any bytes remain" is not the same test. RFC 6455 §5.2
                    // frames carry a length, so a frame is only actionable once
                    // that many payload bytes have arrived; a complete frame
                    // trailed by one byte of the next satisfies "bytes remain"
                    // while offering nothing to process. Deferring on that holds
                    // an answer the client has already earned until the rest of
                    // an unrelated frame arrives, which a client waiting on that
                    // answer before sending more never sends.
                    _hasPendingData = HasCompleteFrame(buffer);
                    gotMessage = true;
                    break;
                }
            }

            // AdvanceTo(consumed) tells the pipe "everything up to consumed is done; give me the rest
            // immediately." AdvanceTo(consumed, buffer.End) tells it to wait for NEW data past buffer.End.
            if (gotMessage)
                _reader.AdvanceTo(consumed);
            else
            {
                _reader.AdvanceTo(consumed, buffer.End);
                if (result.IsCompleted) return null;
            }
        }

        return new WebSocketMessage(
            messageOpcode == WebSocketOpcode.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            _messageBuffer.WrittenMemory);
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

        await _reader.CompleteAsync();
        await _writer.CompleteAsync();
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
