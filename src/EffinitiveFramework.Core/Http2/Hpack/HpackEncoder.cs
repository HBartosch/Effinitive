using System.Buffers;
using System.Text;

namespace EffinitiveFramework.Core.Http2.Hpack;

/// <summary>
/// HPACK header compression encoder
/// </summary>
public class HpackEncoder
{
    private readonly HpackDynamicTable _dynamicTable;
    
    public HpackEncoder(int maxDynamicTableSize = 4096)
    {
        _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
    }
    
    /// <summary>
    /// Encode headers using HPACK compression
    /// </summary>
    public byte[] EncodeHeaders(List<(string name, string value)> headers)
    {
        var buffer = new ArrayBufferWriter<byte>();
        
        foreach (var (name, value) in headers)
        {
            var staticIndex = FindInStaticTable(name, value);
            
            if (staticIndex > 0)
            {
                // Indexed header field representation
                EncodeInteger(buffer, staticIndex, 7, 0x80);
            }
            else
            {
                var nameIndex = FindNameInStaticTable(name);
                
                if (nameIndex > 0)
                {
                    // Literal header field with incremental indexing - indexed name
                    EncodeInteger(buffer, nameIndex, 6, 0x40);
                    EncodeString(buffer, value, huffman: false);
                }
                else
                {
                    // Literal header field with incremental indexing - new name
                    buffer.GetSpan(1)[0] = 0x40;
                    buffer.Advance(1);
                    EncodeString(buffer, name, huffman: false);
                    EncodeString(buffer, value, huffman: false);
                }
                
                _dynamicTable.Add(name, value);
            }
        }
        
        return buffer.WrittenSpan.ToArray();
    }
    
    private void EncodeInteger(IBufferWriter<byte> buffer, int value, int prefixBits, byte prefix)
    {
        var maxPrefixValue = (1 << prefixBits) - 1;
        
        if (value < maxPrefixValue)
        {
            buffer.GetSpan(1)[0] = (byte)(prefix | value);
            buffer.Advance(1);
        }
        else
        {
            buffer.GetSpan(1)[0] = (byte)(prefix | maxPrefixValue);
            buffer.Advance(1);
            
            value -= maxPrefixValue;
            
            while (value >= 128)
            {
                buffer.GetSpan(1)[0] = (byte)((value & 0x7F) | 0x80);
                buffer.Advance(1);
                value >>= 7;
            }
            
            buffer.GetSpan(1)[0] = (byte)value;
            buffer.Advance(1);
        }
    }
    
    private void EncodeString(IBufferWriter<byte> buffer, string value, bool huffman)
    {
        byte[] bytes;
        byte prefix;
        
        if (huffman)
        {
            // Use Huffman encoding for compression
            bytes = HuffmanEncoder.Encode(value);
            prefix = 0x80; // Set Huffman flag
        }
        else
        {
            // Use literal encoding
            bytes = Encoding.ASCII.GetBytes(value);
            prefix = 0x00;
        }
        
        EncodeInteger(buffer, bytes.Length, 7, prefix);
        
        var span = buffer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        buffer.Advance(bytes.Length);
    }
    
    private int FindInStaticTable(string name, string value)
    {
        for (int i = 0; i < HpackStaticTable.Entries.Length; i++)
        {
            var entry = HpackStaticTable.Entries[i];
            if (entry.name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                entry.value.Equals(value, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }
        return 0;
    }
    
    private int FindNameInStaticTable(string name)
    {
        for (int i = 0; i < HpackStaticTable.Entries.Length; i++)
        {
            if (HpackStaticTable.Entries[i].name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }
        return 0;
    }
}
