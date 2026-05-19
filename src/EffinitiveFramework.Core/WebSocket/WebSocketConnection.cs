using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;

namespace EffinitiveFramework.Core.WebSocket;

/// <summary>
/// Represents a WebSocket connection that has been upgraded from HTTP/1.1.
/// Implements RFC 6455 frame read/write, ping/pong, and close handshake.
/// </summary>
public sealed class WebSocketConnection : IAsyncDisposable
{
    private static readonly byte[] WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"u8.ToArray();

    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly byte[] _writeBuffer;
    private bool _closeSent;
    private bool _closeReceived;

    /// <summary>
    /// Whether the WebSocket connection is still open.
    /// </summary>
    public bool IsOpen => !_closeSent && !_closeReceived;

    internal WebSocketConnection(Stream stream)
    {
        _stream = stream;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 4096, leaveOpen: true));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(minimumBufferSize: 4096, leaveOpen: true));
        _writeBuffer = new byte[WebSocketFrame.MaxFrameSize(65536)]; // Reusable write buffer
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
        WebSocketOpcode messageOpcode = default;
        var messageBuffer = new ArrayBufferWriter<byte>();
        bool firstFrame = true;

        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            while (WebSocketFrame.TryParse(buffer, out var frame, out var consumed))
            {
                buffer = buffer.Slice(consumed);

                // Handle control frames inline
                if (frame.IsControl)
                {
                    switch (frame.Opcode)
                    {
                        case WebSocketOpcode.Ping:
                            await SendPongAsync(frame.Payload, cancellationToken);
                            continue;

                        case WebSocketOpcode.Pong:
                            // Unsolicited pong — ignore per RFC 6455 §5.5.3
                            continue;

                        case WebSocketOpcode.Close:
                            _closeReceived = true;
                            if (!_closeSent)
                            {
                                // Echo close frame back
                                await SendCloseAsync(1000, null, cancellationToken);
                            }
                            _reader.AdvanceTo(consumed);
                            return null;
                    }
                }

                // Data frame
                if (firstFrame)
                {
                    messageOpcode = frame.Opcode;
                    firstFrame = false;
                }

                messageBuffer.Write(frame.Payload.Span);

                if (frame.Fin)
                {
                    _reader.AdvanceTo(consumed);
                    return new WebSocketMessage(
                        messageOpcode == WebSocketOpcode.Text
                            ? WebSocketMessageType.Text
                            : WebSocketMessageType.Binary,
                        messageBuffer.WrittenMemory);
                }
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                return null; // Connection closed without close frame
        }
    }

    /// <summary>
    /// Send a message to the client.
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken = default)
    {
        var opcode = type == WebSocketMessageType.Text ? WebSocketOpcode.Text : WebSocketOpcode.Binary;

        // For small messages, use the pre-allocated buffer
        if (data.Length <= 65536)
        {
            var frameSize = WebSocketFrame.WriteFrame(_writeBuffer, opcode, data.Span);
            _writer.Write(_writeBuffer.AsSpan(0, frameSize));
            await _writer.FlushAsync(cancellationToken);
        }
        else
        {
            // Large messages: allocate
            var buf = new byte[WebSocketFrame.MaxFrameSize(data.Length)];
            var frameSize = WebSocketFrame.WriteFrame(buf, opcode, data.Span);
            _writer.Write(buf.AsSpan(0, frameSize));
            await _writer.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Send a close frame and close the connection.
    /// </summary>
    public async ValueTask SendCloseAsync(ushort statusCode, string? reason, CancellationToken cancellationToken = default)
    {
        if (_closeSent)
            return;

        _closeSent = true;

        // Build close payload: 2 bytes status + optional UTF-8 reason
        var reasonBytes = reason != null ? Encoding.UTF8.GetBytes(reason) : Array.Empty<byte>();
        var payload = new byte[2 + reasonBytes.Length];
        payload[0] = (byte)(statusCode >> 8);
        payload[1] = (byte)statusCode;
        if (reasonBytes.Length > 0)
            reasonBytes.CopyTo(payload, 2);

        var frameSize = WebSocketFrame.WriteFrame(_writeBuffer, WebSocketOpcode.Close, payload);
        _writer.Write(_writeBuffer.AsSpan(0, frameSize));
        await _writer.FlushAsync(cancellationToken);
    }

    private async ValueTask SendPongAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frameSize = WebSocketFrame.WriteFrame(_writeBuffer, WebSocketOpcode.Pong, payload.Span);
        _writer.Write(_writeBuffer.AsSpan(0, frameSize));
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
