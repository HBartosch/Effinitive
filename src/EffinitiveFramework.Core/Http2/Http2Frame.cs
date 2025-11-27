using System.Buffers.Binary;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Represents an HTTP/2 frame
/// </summary>
public struct Http2Frame
{
    public int Length { get; set; }
    public byte Type { get; set; }
    public byte Flags { get; set; }
    public int StreamId { get; set; }
    public Memory<byte> Payload { get; set; }
    
    /// <summary>
    /// Parse frame header from buffer
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out Http2Frame frame)
    {
        frame = default;
        
        if (buffer.Length < Http2Constants.FrameHeaderLength)
            return false;
        
        // Length (24 bits)
        frame.Length = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
        
        // Type (8 bits)
        frame.Type = buffer[3];
        
        // Flags (8 bits)
        frame.Flags = buffer[4];
        
        // Stream ID (31 bits, R bit reserved)
        frame.StreamId = BinaryPrimitives.ReadInt32BigEndian(buffer[5..9]) & Http2Constants.StreamIdMask;
        
        return true;
    }
    
    /// <summary>
    /// Write frame header to buffer
    /// </summary>
    public void WriteHeader(Span<byte> buffer)
    {
        if (buffer.Length < Http2Constants.FrameHeaderLength)
            throw new ArgumentException("Buffer too small for frame header");
        
        // Length (24 bits)
        buffer[0] = (byte)(Length >> 16);
        buffer[1] = (byte)(Length >> 8);
        buffer[2] = (byte)Length;
        
        // Type
        buffer[3] = Type;
        
        // Flags
        buffer[4] = Flags;
        
        // Stream ID (31 bits)
        BinaryPrimitives.WriteInt32BigEndian(buffer[5..9], StreamId & Http2Constants.StreamIdMask);
    }
    
    /// <summary>
    /// Check if flag is set
    /// </summary>
    public bool HasFlag(byte flag) => (Flags & flag) != 0;
    
    /// <summary>
    /// Set a flag
    /// </summary>
    public void SetFlag(byte flag) => Flags |= flag;
    
    /// <summary>
    /// Clear a flag
    /// </summary>
    public void ClearFlag(byte flag) => Flags &= (byte)~flag;
}
