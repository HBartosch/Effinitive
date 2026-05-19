using System.Text;
using EffinitiveFramework.Core.Http2.Hpack;

namespace EffinitiveFramework.Core.Http3;

/// <summary>
/// QPACK decoder for HTTP/3 (RFC 9204).
/// Supports static table only (no dynamic table insertions).
/// </summary>
internal sealed class QpackDecoder
{
    public List<(string name, string value)> Decode(ReadOnlySpan<byte> headerBlock)
    {
        var headers = new List<(string, string)>();
        if (headerBlock.Length < 2)
            return headers;

        var offset = 0;

        // Header Block Prefix (RFC 9204 §4.5.1)
        // Required Insert Count (8-bit prefix integer)
        _ = DecodeInteger(headerBlock, ref offset, 8);
        // Sign bit + Delta Base (7-bit prefix integer)
        _ = DecodeInteger(headerBlock, ref offset, 7);

        // Decode header field representations
        while (offset < headerBlock.Length)
        {
            var b = headerBlock[offset];

            if ((b & 0x80) != 0)
            {
                // Indexed Field Line (RFC 9204 §4.5.2): 1 T index
                var isStatic = (b & 0x40) != 0;
                var index = DecodeInteger(headerBlock, ref offset, 6);
                if (isStatic && index < QpackStaticTable.Entries.Length)
                {
                    var entry = QpackStaticTable.Entries[index];
                    headers.Add((entry.name, entry.value));
                }
            }
            else if ((b & 0xC0) == 0x40)
            {
                // Literal Field Line With Name Reference (RFC 9204 §4.5.4): 01 N T index
                var isStatic = (b & 0x10) != 0;
                var nameIndex = DecodeInteger(headerBlock, ref offset, 4);
                var value = DecodeString(headerBlock, ref offset);

                string name;
                if (isStatic && nameIndex < QpackStaticTable.Entries.Length)
                    name = QpackStaticTable.Entries[nameIndex].name;
                else
                    name = $"unknown-{nameIndex}";

                headers.Add((name, value));
            }
            else if ((b & 0xE0) == 0x20)
            {
                // Literal Field Line With Literal Name (RFC 9204 §4.5.6): 001 N name value
                offset++; // skip pattern byte — N bit is in bit 4
                var nameLen = DecodeInteger(headerBlock, ref offset, 3);
                // Actually, re-read: pattern is 001N HLEN, let me redo this
                // Back up and parse correctly
                offset--;
                var name = DecodeLiteralName(headerBlock, ref offset);
                var value = DecodeString(headerBlock, ref offset);
                headers.Add((name, value));
            }
            else if ((b & 0xF0) == 0x10)
            {
                // Indexed Field Line With Post-Base Index (RFC 9204 §4.5.3): 0001 index
                // Dynamic table — skip
                _ = DecodeInteger(headerBlock, ref offset, 4);
            }
            else
            {
                // Literal Field Line With Post-Base Name Reference (RFC 9204 §4.5.5): 0000 N index
                var nameIndex = DecodeInteger(headerBlock, ref offset, 3);
                var value = DecodeString(headerBlock, ref offset);
                headers.Add(($"post-base-{nameIndex}", value));
            }
        }

        return headers;
    }

    private static string DecodeLiteralName(ReadOnlySpan<byte> data, ref int offset)
    {
        var b = data[offset];
        // 001N H LEN  — N is bit 4, H is bit 3
        var huffman = (b & 0x08) != 0;
        var nameLen = DecodeInteger(data, ref offset, 3);

        if (nameLen == 0)
            return string.Empty;

        var nameBytes = data.Slice(offset, (int)nameLen);
        offset += (int)nameLen;

        if (huffman)
            return HuffmanDecoder.Decode(nameBytes);

        return Encoding.ASCII.GetString(nameBytes);
    }

    private static string DecodeString(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
            return string.Empty;

        var b = data[offset];
        var huffman = (b & 0x80) != 0;
        var length = DecodeInteger(data, ref offset, 7);

        if (length == 0)
            return string.Empty;

        var strBytes = data.Slice(offset, (int)length);
        offset += (int)length;

        if (huffman)
            return HuffmanDecoder.Decode(strBytes);

        return Encoding.ASCII.GetString(strBytes);
    }

    private static long DecodeInteger(ReadOnlySpan<byte> data, ref int offset, int prefixBits)
    {
        var mask = (1 << prefixBits) - 1;
        var value = (long)(data[offset] & mask);
        offset++;

        if (value < mask)
            return value;

        // Multi-byte integer
        long m = 0;
        while (offset < data.Length)
        {
            var b = data[offset];
            offset++;
            value += (long)(b & 0x7F) << (int)m;
            m += 7;
            if ((b & 0x80) == 0)
                break;
        }

        return value;
    }
}

/// <summary>
/// QPACK encoder for HTTP/3 (RFC 9204).
/// Encodes response headers using static table references where possible.
/// No dynamic table (Required Insert Count = 0).
/// </summary>
internal sealed class QpackEncoder
{
    // Pre-built lookup: (name, value) → static table index for full matches
    private static readonly Dictionary<(string, string), int> _fullMatch = new();
    // Pre-built lookup: name → static table index for name-only matches
    private static readonly Dictionary<string, int> _nameMatch = new();

    static QpackEncoder()
    {
        for (int i = 0; i < QpackStaticTable.Entries.Length; i++)
        {
            var (name, value) = QpackStaticTable.Entries[i];
            _fullMatch.TryAdd((name, value), i);
            _nameMatch.TryAdd(name, i);
        }
    }

    public byte[] Encode(List<(string name, string value)> headers)
    {
        using var ms = new MemoryStream(256);

        // Header Block Prefix: Required Insert Count = 0, Delta Base = 0
        ms.WriteByte(0x00); // Required Insert Count = 0
        ms.WriteByte(0x00); // Sign=0, Delta Base = 0

        foreach (var (name, value) in headers)
        {
            if (_fullMatch.TryGetValue((name, value), out var fullIdx))
            {
                // Indexed Field Line (static): 1 T=1 index
                EncodeIndexed(ms, fullIdx);
            }
            else if (_nameMatch.TryGetValue(name, out var nameIdx))
            {
                // Literal Field Line With Name Reference (static): 01 N=0 T=1 index + value
                EncodeLiteralWithNameRef(ms, nameIdx, value);
            }
            else
            {
                // Literal Field Line With Literal Name
                EncodeLiteralWithLiteralName(ms, name, value);
            }
        }

        return ms.ToArray();
    }

    private static void EncodeIndexed(MemoryStream ms, int index)
    {
        // Pattern: 1 1 index (6-bit prefix) — T=1 for static
        EncodeInteger(ms, index, 6, 0xC0);
    }

    private static void EncodeLiteralWithNameRef(MemoryStream ms, int nameIndex, string value)
    {
        // Pattern: 01 N=0 T=1 index (4-bit prefix)
        EncodeInteger(ms, nameIndex, 4, 0x50);
        // Value: H=0 length (7-bit prefix) + literal
        EncodeString(ms, value);
    }

    private static void EncodeLiteralWithLiteralName(MemoryStream ms, string name, string value)
    {
        // Pattern: 001 N=0 (then H + name length with 3-bit prefix)
        // First byte: 0010 H LLL
        var nameBytes = Encoding.ASCII.GetBytes(name);
        EncodeInteger(ms, nameBytes.Length, 3, 0x20);
        ms.Write(nameBytes);
        EncodeString(ms, value);
    }

    private static void EncodeString(MemoryStream ms, string value)
    {
        // H=0 (no Huffman), length (7-bit prefix)
        var bytes = Encoding.ASCII.GetBytes(value);
        EncodeInteger(ms, bytes.Length, 7, 0x00);
        ms.Write(bytes);
    }

    private static void EncodeInteger(MemoryStream ms, int value, int prefixBits, byte pattern)
    {
        var mask = (1 << prefixBits) - 1;
        if (value < mask)
        {
            ms.WriteByte((byte)(pattern | value));
        }
        else
        {
            ms.WriteByte((byte)(pattern | mask));
            value -= mask;
            while (value >= 128)
            {
                ms.WriteByte((byte)(0x80 | (value & 0x7F)));
                value >>= 7;
            }
            ms.WriteByte((byte)value);
        }
    }
}
