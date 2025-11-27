using System.Text;

namespace EffinitiveFramework.Core.Http2.Hpack;

/// <summary>
/// HPACK header compression decoder
/// RFC 7541: https://tools.ietf.org/html/rfc7541
/// </summary>
public class HpackDecoder
{
    private readonly HpackDynamicTable _dynamicTable;
    private readonly int _maxDecompressedSize;
    
    public HpackDecoder(int maxDynamicTableSize = 4096, int maxDecompressedSize = 8192)
    {
        _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
        _maxDecompressedSize = maxDecompressedSize;
    }
    
    /// <summary>
    /// Decode HPACK-encoded headers
    /// </summary>
    public List<(string name, string value)> DecodeHeaders(ReadOnlySpan<byte> encoded)
    {
        var headers = new List<(string, string)>();
        var offset = 0;
        var totalDecompressedSize = 0; // SECURITY: Track decompressed size
        
        while (offset < encoded.Length)
        {
            var b = encoded[offset];
            
            if ((b & 0x80) != 0)
            {
                // Indexed header field
                var index = DecodeInteger(encoded, ref offset, 7);
                var (name, value) = GetIndexedHeader(index);
                
                // SECURITY: Check decompressed size (prevents HPACK bombs)
                totalDecompressedSize += name.Length + value.Length;
                if (totalDecompressedSize > _maxDecompressedSize)
                {
                    throw new InvalidOperationException($"HPACK decompression size {totalDecompressedSize} exceeds maximum {_maxDecompressedSize}");
                }
                
                headers.Add((name, value));
            }
            else if ((b & 0x40) != 0)
            {
                // Literal header field with incremental indexing
                var (name, value) = DecodeLiteralHeader(encoded, ref offset, 6, addToTable: true);
                
                // SECURITY: Check decompressed size
                totalDecompressedSize += name.Length + value.Length;
                if (totalDecompressedSize > _maxDecompressedSize)
                {
                    throw new InvalidOperationException($"HPACK decompression size {totalDecompressedSize} exceeds maximum {_maxDecompressedSize}");
                }
                
                headers.Add((name, value));
            }
            else if ((b & 0x20) != 0)
            {
                // Dynamic table size update
                var newSize = DecodeInteger(encoded, ref offset, 5);
                _dynamicTable.UpdateMaxSize((int)newSize);
            }
            else
            {
                // Literal header field without indexing or never indexed
                var prefixBits = (b & 0x10) != 0 ? 4 : 4;
                var (name, value) = DecodeLiteralHeader(encoded, ref offset, prefixBits, addToTable: false);
                
                // SECURITY: Check decompressed size
                totalDecompressedSize += name.Length + value.Length;
                if (totalDecompressedSize > _maxDecompressedSize)
                {
                    throw new InvalidOperationException($"HPACK decompression size {totalDecompressedSize} exceeds maximum {_maxDecompressedSize}");
                }
                
                headers.Add((name, value));
            }
        }
        
        return headers;
    }
    
    private int DecodeInteger(ReadOnlySpan<byte> data, ref int offset, int prefixBits)
    {
        var mask = (1 << prefixBits) - 1;
        var value = data[offset] & mask;
        offset++;
        
        if (value < mask)
            return value;
        
        var m = 0;
        while (offset < data.Length)
        {
            var b = data[offset++];
            value += (b & 0x7F) << m;
            m += 7;
            
            if ((b & 0x80) == 0)
                break;
        }
        
        return value;
    }
    
    private (string name, string value) DecodeLiteralHeader(ReadOnlySpan<byte> data, ref int offset, int prefixBits, bool addToTable)
    {
        var nameIndex = DecodeInteger(data, ref offset, prefixBits);
        
        string name;
        if (nameIndex > 0)
        {
            (name, _) = GetIndexedHeader(nameIndex);
        }
        else
        {
            name = DecodeString(data, ref offset);
        }
        
        var value = DecodeString(data, ref offset);
        
        if (addToTable)
        {
            _dynamicTable.Add(name, value);
        }
        
        return (name, value);
    }
    
    private string DecodeString(ReadOnlySpan<byte> data, ref int offset)
    {
        var huffmanEncoded = (data[offset] & 0x80) != 0;
        var length = DecodeInteger(data, ref offset, 7);
        
        var stringData = data.Slice(offset, length);
        offset += length;
        
        if (huffmanEncoded)
        {
            return HuffmanDecoder.Decode(stringData);
        }
        else
        {
            return Encoding.ASCII.GetString(stringData);
        }
    }
    
    private (string name, string value) GetIndexedHeader(int index)
    {
        if (index <= HpackStaticTable.Entries.Length)
        {
            return HpackStaticTable.Entries[index - 1];
        }
        else
        {
            return _dynamicTable.Get(index - HpackStaticTable.Entries.Length - 1);
        }
    }
}
