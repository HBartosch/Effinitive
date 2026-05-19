using System.Buffers;
using System.Buffers.Binary;

namespace EffinitiveFramework.Core.WebSocket;

/// <summary>
/// WebSocket opcodes per RFC 6455 §5.2
/// </summary>
public enum WebSocketOpcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA
}

/// <summary>
/// Represents a parsed WebSocket frame (RFC 6455 §5.2).
/// </summary>
public readonly struct WebSocketFrame
{
    public readonly bool Fin;
    public readonly WebSocketOpcode Opcode;
    public readonly bool Masked;
    public readonly ReadOnlyMemory<byte> Payload;

    public WebSocketFrame(bool fin, WebSocketOpcode opcode, bool masked, ReadOnlyMemory<byte> payload)
    {
        Fin = fin;
        Opcode = opcode;
        Masked = masked;
        Payload = payload;
    }

    public bool IsControl => Opcode >= WebSocketOpcode.Close;

    /// <summary>
    /// Try to parse a WebSocket frame from a ReadOnlySequence.
    /// Returns false if the buffer doesn't contain a complete frame.
    /// </summary>
    public static bool TryParse(ReadOnlySequence<byte> buffer, out WebSocketFrame frame, out SequencePosition consumed)
    {
        frame = default;
        consumed = buffer.Start;

        if (buffer.Length < 2)
            return false;

        Span<byte> header = stackalloc byte[14]; // max frame header
        var reader = new SequenceReader<byte>(buffer);

        // Byte 0: FIN + RSV + Opcode
        reader.TryRead(out byte b0);
        bool fin = (b0 & 0x80) != 0;
        var opcode = (WebSocketOpcode)(b0 & 0x0F);

        // Byte 1: MASK + Payload length
        reader.TryRead(out byte b1);
        bool masked = (b1 & 0x80) != 0;
        long payloadLength = b1 & 0x7F;

        if (payloadLength == 126)
        {
            if (reader.Remaining < 2)
                return false;
            reader.TryReadBigEndian(out short len16);
            payloadLength = (ushort)len16;
        }
        else if (payloadLength == 127)
        {
            if (reader.Remaining < 8)
                return false;
            reader.TryReadBigEndian(out long len64);
            payloadLength = len64;
        }

        // Masking key (4 bytes if masked)
        Span<byte> maskKey = stackalloc byte[4];
        if (masked)
        {
            if (reader.Remaining < 4)
                return false;
            reader.TryCopyTo(maskKey);
            reader.Advance(4);
        }

        // Payload
        if (reader.Remaining < payloadLength)
            return false;

        var payload = new byte[payloadLength];
        reader.TryCopyTo(payload);
        reader.Advance(payloadLength);

        // Unmask payload if masked (client-to-server messages MUST be masked per RFC 6455 §5.3)
        if (masked)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= maskKey[i & 3];
        }

        frame = new WebSocketFrame(fin, opcode, masked, payload);
        consumed = reader.Position;
        return true;
    }

    /// <summary>
    /// Write a WebSocket frame to a buffer. Server-to-client frames are NOT masked per RFC 6455 §5.1.
    /// </summary>
    public static int WriteFrame(Span<byte> destination, WebSocketOpcode opcode, ReadOnlySpan<byte> payload, bool fin = true)
    {
        int offset = 0;

        // Byte 0: FIN + Opcode
        destination[offset++] = (byte)((fin ? 0x80 : 0x00) | (byte)opcode);

        // Byte 1+: Payload length (no mask for server-to-client)
        if (payload.Length < 126)
        {
            destination[offset++] = (byte)payload.Length;
        }
        else if (payload.Length <= 65535)
        {
            destination[offset++] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset), (ushort)payload.Length);
            offset += 2;
        }
        else
        {
            destination[offset++] = 127;
            BinaryPrimitives.WriteInt64BigEndian(destination.Slice(offset), payload.Length);
            offset += 8;
        }

        // Payload data
        payload.CopyTo(destination.Slice(offset));
        offset += payload.Length;

        return offset;
    }

    /// <summary>
    /// Calculate the maximum frame size needed for a given payload.
    /// </summary>
    public static int MaxFrameSize(int payloadLength)
    {
        // 2 bytes header + up to 8 bytes extended length + payload
        return 2 + 8 + payloadLength;
    }
}
