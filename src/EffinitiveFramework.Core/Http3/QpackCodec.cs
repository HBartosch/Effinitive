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
                // Literal Field Line With Literal Name (RFC 9204 §4.5.6): 001 N H name-length.
                // DecodeLiteralName reads the pattern byte itself, because the H bit and the
                // length share it, so offset must still be on that byte when it is called.
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

    /// <summary>
    /// Encodes a field section into <paramref name="destination"/>, returning false if it does
    /// not fit, in which case nothing has been written and the caller should retry with more room.
    /// </summary>
    /// <remarks>
    /// Written into a span the caller owns rather than returned as a fresh array. Building this
    /// through a MemoryStream cost the stream, its buffer, a byte array for every field name and
    /// every field value, and a final copy out: about a dozen allocations for a response carrying
    /// five fields. Encoding ASCII straight into the destination costs none of them.
    /// </remarks>
    public static bool TryEncode(List<(string name, string value)> headers, Span<byte> destination, out int written)
    {
        written = 0;

        // Field Section Prefix: Required Insert Count = 0, Delta Base = 0 (RFC 9204 §4.5.1).
        // Both zero because only the static table is used, so no dynamic entries are referenced.
        if (!TryWriteByte(destination, ref written, 0x00)) return false;
        if (!TryWriteByte(destination, ref written, 0x00)) return false;

        foreach (var (name, value) in headers)
        {
            if (_fullMatch.TryGetValue((name, value), out var fullIdx))
            {
                // Indexed Field Line (static): 1 T=1 index
                if (!TryWriteInteger(destination, ref written, fullIdx, 6, 0xC0)) return false;
            }
            else if (_nameMatch.TryGetValue(name, out var nameIdx))
            {
                // Literal Field Line With Name Reference (static): 01 N=0 T=1 index
                if (!TryWriteInteger(destination, ref written, nameIdx, 4, 0x50)) return false;
                if (!TryWriteString(destination, ref written, value)) return false;
            }
            else
            {
                // Literal Field Line With Literal Name: 0010 H LLL
                if (!TryWriteInteger(destination, ref written, name.Length, 3, 0x20)) return false;
                if (!TryWriteAscii(destination, ref written, name)) return false;
                if (!TryWriteString(destination, ref written, value)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Encodes a response's field section directly, without building a list of pairs first.
    /// </summary>
    /// <remarks>
    /// Assembling the fields into a List to hand here cost the list, a string for the status and
    /// a string for the content length, every response. None of those outlive the encoding, and
    /// none of them are needed: the status comes from a table the process already holds, and the
    /// content length is written as digits straight into the destination.
    /// </remarks>
    public static bool TryEncodeResponse(
        Http.HttpResponse response, int bodyLength, Span<byte> destination, out int written)
    {
        written = 0;

        // Field Section Prefix: Required Insert Count = 0, Delta Base = 0 (RFC 9204 §4.5.1).
        if (!TryWriteByte(destination, ref written, 0x00)) return false;
        if (!TryWriteByte(destination, ref written, 0x00)) return false;

        if (!TryWriteField(destination, ref written, ":status", Http.WellKnownTokens.StatusCode(response.StatusCode)))
            return false;

        if (!string.IsNullOrEmpty(response.ContentType)
            && !TryWriteField(destination, ref written, "content-type", response.ContentType))
            return false;

        if (bodyLength > 0 && !TryWriteContentLength(destination, ref written, bodyLength))
            return false;

        if (response.Headers != null)
        {
            foreach (var header in response.Headers)
            {
                if (!TryWriteField(destination, ref written,
                        Http.WellKnownTokens.Lowercase(header.Key), header.Value))
                    return false;
            }
        }

        return true;
    }

    private static bool TryWriteField(Span<byte> destination, ref int written, string name, string value)
    {
        if (_fullMatch.TryGetValue((name, value), out var fullIdx))
            return TryWriteInteger(destination, ref written, fullIdx, 6, 0xC0);

        if (_nameMatch.TryGetValue(name, out var nameIdx))
        {
            return TryWriteInteger(destination, ref written, nameIdx, 4, 0x50)
                && TryWriteString(destination, ref written, value);
        }

        return TryWriteInteger(destination, ref written, name.Length, 3, 0x20)
            && TryWriteAscii(destination, ref written, name)
            && TryWriteString(destination, ref written, value);
    }

    /// <summary>Writes content-length with its value formatted straight into the destination.</summary>
    private static bool TryWriteContentLength(Span<byte> destination, ref int written, int bodyLength)
    {
        if (!_nameMatch.TryGetValue("content-length", out var nameIdx))
            return false;

        if (!TryWriteInteger(destination, ref written, nameIdx, 4, 0x50)) return false;

        Span<char> digits = stackalloc char[11];
        if (!bodyLength.TryFormat(digits, out var digitCount)) return false;

        if (!TryWriteInteger(destination, ref written, digitCount, 7, 0x00)) return false;
        if (destination.Length - written < digitCount) return false;

        for (int i = 0; i < digitCount; i++)
            destination[written + i] = (byte)digits[i];

        written += digitCount;
        return true;
    }

    private static bool TryWriteByte(Span<byte> destination, ref int written, byte value)
    {
        if (written >= destination.Length) return false;
        destination[written++] = value;
        return true;
    }

    /// <summary>Writes a string with H=0 and a 7-bit length prefix.</summary>
    private static bool TryWriteString(Span<byte> destination, ref int written, string value)
    {
        if (!TryWriteInteger(destination, ref written, value.Length, 7, 0x00)) return false;
        return TryWriteAscii(destination, ref written, value);
    }

    private static bool TryWriteAscii(Span<byte> destination, ref int written, string value)
    {
        // One ASCII byte per char, so the character count is the byte count and the room needed
        // is known without encoding first.
        if (destination.Length - written < value.Length) return false;
        written += System.Text.Encoding.ASCII.GetBytes(value, destination[written..]);
        return true;
    }

    private static bool TryWriteInteger(Span<byte> destination, ref int written, int value, int prefixBits, byte pattern)
    {
        var mask = (1 << prefixBits) - 1;
        if (value < mask)
            return TryWriteByte(destination, ref written, (byte)(pattern | value));

        if (!TryWriteByte(destination, ref written, (byte)(pattern | mask))) return false;

        value -= mask;
        while (value >= 128)
        {
            if (!TryWriteByte(destination, ref written, (byte)(0x80 | (value & 0x7F)))) return false;
            value >>= 7;
        }

        return TryWriteByte(destination, ref written, (byte)value);
    }
}
