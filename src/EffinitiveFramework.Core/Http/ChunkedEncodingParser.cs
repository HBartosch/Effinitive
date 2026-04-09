using System.Buffers;
using System.Text;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Chunked transfer encoding parser for HTTP/1.1 (RFC 9112 §7.1)
/// Handles chunked request/response bodies with strict validation.
/// </summary>
public static class ChunkedEncodingParser
{
    private static readonly byte Cr = (byte)'\r';
    private static readonly byte Lf = (byte)'\n';
    private const int MaxChunkExtensionLength = 16384; // 16 KB limit for chunk extensions

    /// <summary>
    /// Parse chunked transfer encoding from buffer.
    /// Returns true if complete, false if more data needed.
    /// Throws HttpParseException for malformed chunks.
    /// </summary>
    public static bool TryParseChunked(
        ref ReadOnlySequence<byte> buffer,
        out byte[] body,
        out SequencePosition consumed,
        out int bytesConsumed,
        int maxBodySize = 30 * 1024 * 1024)
    {
        body = Array.Empty<byte>();
        consumed = buffer.Start;
        bytesConsumed = 0;

        var reader = new SequenceReader<byte>(buffer);
        var chunks = new List<byte[]>();
        var totalSize = 0;

        while (true)
        {
            // Read chunk size line - scan for line ending (CRLF or bare LF)
            if (!TryReadChunkLine(ref reader, out var chunkSizeBytes))
            {
                return false; // Need more data
            }

            // Parse chunk size line: chunk-size [ chunk-ext ] CRLF
            // chunk-size = 1*HEXDIG
            // chunk-ext = *( BWS ";" BWS chunk-ext-name [ "=" chunk-ext-val ] )
            var chunkSizeStr = Encoding.ASCII.GetString(chunkSizeBytes);

            // Validate and extract chunk extensions
            var semicolonIndex = chunkSizeStr.IndexOf(';');
            if (semicolonIndex >= 0)
            {
                var extPart = chunkSizeStr.Substring(semicolonIndex + 1);
                chunkSizeStr = chunkSizeStr.Substring(0, semicolonIndex);

                // Reject excessively long chunk extensions (DoS protection)
                if (extPart.Length > MaxChunkExtensionLength)
                    throw HttpParseException.BadRequest("Chunk extension too long");

                // Validate extension: must have name (token chars), optionally = value
                ValidateChunkExtension(extPart);
            }

            // chunk-size must not have leading/trailing whitespace
            if (chunkSizeStr.Length > 0 && (chunkSizeStr[0] == ' ' || chunkSizeStr[0] == '\t'))
                throw HttpParseException.BadRequest("Leading whitespace in chunk size");
            if (chunkSizeStr.Length > 0 && (chunkSizeStr[^1] == ' ' || chunkSizeStr[^1] == '\t'))
                throw HttpParseException.BadRequest("Trailing whitespace in chunk size");

            if (chunkSizeStr.Length == 0)
                throw HttpParseException.BadRequest("Empty chunk size");

            if (!int.TryParse(chunkSizeStr, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
            {
                throw HttpParseException.BadRequest($"Invalid chunk size: {chunkSizeStr}");
            }

            if (chunkSize < 0)
                throw HttpParseException.BadRequest($"Negative chunk size: {chunkSize}");

            // Last chunk (size = 0)
            if (chunkSize == 0)
            {
                // Read trailer headers (until empty line: CRLF or bare LF)
                while (true)
                {
                    if (!TryReadChunkLine(ref reader, out var trailerLine))
                    {
                        return false; // Need more data for trailers
                    }
                    // Empty line = end of trailers
                    if (trailerLine.Length == 0)
                        break;

                    // RFC 9110 §6.5.1: Reject prohibited trailer fields
                    var colonIdx = trailerLine.IndexOf((byte)':');
                    if (colonIdx > 0)
                    {
                        var trailerName = System.Text.Encoding.ASCII.GetString(trailerLine.Slice(0, colonIdx)).Trim();
                        if (trailerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Content-Range", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                            trailerName.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase))
                        {
                            throw HttpParseException.BadRequest($"Prohibited trailer field: {trailerName}");
                        }
                    }
                }

                // Combine all chunks
                body = new byte[totalSize];
                var offset = 0;
                foreach (var chunk in chunks)
                {
                    Buffer.BlockCopy(chunk, 0, body, offset, chunk.Length);
                    offset += chunk.Length;
                }

                consumed = reader.Position;
                bytesConsumed = (int)reader.Consumed;
                return true;
            }

            // SECURITY: Check size limit
            if (totalSize + chunkSize > maxBodySize)
            {
                throw new InvalidOperationException($"Chunked body size {totalSize + chunkSize} exceeds maximum allowed size {maxBodySize}");
            }

            // Read chunk data
            if (reader.Remaining < chunkSize)
            {
                return false; // Need more data
            }

            var chunkData = new byte[chunkSize];
            reader.UnreadSequence.Slice(0, chunkSize).CopyTo(chunkData);
            chunks.Add(chunkData);
            totalSize += chunkSize;
            reader.Advance(chunkSize);

            // Read CRLF or bare LF after chunk data
            if (!TrySkipLineEnding(ref reader))
            {
                return false; // Need more data
            }
        }
    }

    /// <summary>
    /// Read a line from the chunk stream, handling both CRLF and bare LF.
    /// Returns the line content (without line endings).
    /// </summary>
    private static bool TryReadChunkLine(ref SequenceReader<byte> reader, out ReadOnlySpan<byte> line)
    {
        line = default;
        var saved = reader.Position;
        long savedConsumed = reader.Consumed;

        // Try to find CR or LF
        while (reader.TryRead(out byte b))
        {
            if (b == Cr)
            {
                // Found CR — line is everything before it
                long lineLen = reader.Consumed - savedConsumed - 1; // -1 for the CR we just read
                reader.Rewind(reader.Consumed - savedConsumed); // Rewind to start
                if (lineLen > 0)
                {
                    var tempSpan = reader.UnreadSpan;
                    if (tempSpan.Length >= lineLen)
                    {
                        line = tempSpan.Slice(0, (int)lineLen);
                    }
                    else
                    {
                        // Multi-segment; fall back to copy
                        var buf = new byte[lineLen];
                        reader.UnreadSequence.Slice(0, lineLen).CopyTo(buf);
                        line = buf;
                    }
                }
                else
                {
                    line = ReadOnlySpan<byte>.Empty;
                }
                reader.Advance(lineLen + 1); // Skip past line content + CR

                // Must be followed by LF — bare CR is rejected
                if (!reader.TryPeek(out byte next))
                    return false; // Need more data
                if (next == Lf)
                {
                    reader.Advance(1);
                    return true;
                }
                // Bare CR without LF — malformed
                throw HttpParseException.BadRequest("Bare CR in chunked encoding (expected CRLF)");
            }
            else if (b == Lf)
            {
                // Bare LF — reject for security (smuggling prevention)
                throw HttpParseException.BadRequest("Bare LF in chunked encoding line (expected CRLF)");
            }
        }

        // Didn't find a line ending — rewind and report need more data
        reader.Rewind(reader.Consumed - savedConsumed);
        return false;
    }

    /// <summary>
    /// Skip a line ending (CRLF or bare LF) after chunk data.
    /// Bare CR (without LF) is rejected as malformed.
    /// </summary>
    private static bool TrySkipLineEnding(ref SequenceReader<byte> reader)
    {
        if (!reader.TryPeek(out byte b))
            return false;

        if (b == Cr)
        {
            reader.Advance(1);
            if (!reader.TryPeek(out byte next))
                return false; // Need more data
            if (next == Lf)
            {
                reader.Advance(1);
                return true;
            }
            // Bare CR without LF - malformed
            throw HttpParseException.BadRequest("Bare CR in chunked encoding (expected CRLF)");
        }
        else if (b == Lf)
        {
            // Bare LF — reject for security (smuggling prevention)
            throw HttpParseException.BadRequest("Bare LF after chunk data (expected CRLF)");
        }

        // Not a line ending - malformed
        throw HttpParseException.BadRequest("Expected line ending after chunk data");
    }

    /// <summary>
    /// Validate chunk extension per RFC 9112 §7.1.1:
    /// chunk-ext = *( BWS ";" BWS chunk-ext-name [ "=" chunk-ext-val ] )
    /// chunk-ext-name = token
    /// </summary>
    private static void ValidateChunkExtension(string ext)
    {
        var trimmed = ext.Trim();

        // Empty extension after semicolon is invalid
        if (trimmed.Length == 0)
            throw HttpParseException.BadRequest("Empty chunk extension after semicolon");

        // Split on = to get name and optional value
        var eqIdx = trimmed.IndexOf('=');
        var name = eqIdx >= 0 ? trimmed.Substring(0, eqIdx).Trim() : trimmed;

        if (name.Length == 0)
            throw HttpParseException.BadRequest("Empty chunk extension name");

        // Validate extension name is token chars
        foreach (char c in name)
        {
            if (!IsTokenChar((byte)c))
                throw HttpParseException.BadRequest($"Invalid character in chunk extension name: '{c}'");
        }

        // Validate no control characters in the entire extension (including value)
        foreach (char c in ext)
        {
            if (c < 0x20 && c != '\t')
                throw HttpParseException.BadRequest($"Control character in chunk extension: 0x{(int)c:X2}");
            if (c == 0x7F)
                throw HttpParseException.BadRequest("DEL character in chunk extension");
        }
    }

    private static bool IsTokenChar(byte b)
    {
        if (b >= (byte)'A' && b <= (byte)'Z') return true;
        if (b >= (byte)'a' && b <= (byte)'z') return true;
        if (b >= (byte)'0' && b <= (byte)'9') return true;
        return b switch
        {
            (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or
            (byte)'\'' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.' or
            (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~' => true,
            _ => false
        };
    }

    /// <summary>
    /// Encode data in chunked transfer encoding format
    /// </summary>
    public static byte[] EncodeChunked(ReadOnlySpan<byte> data, int chunkSize = 8192)
    {
        if (data.Length == 0)
        {
            // Just the last chunk
            return Encoding.ASCII.GetBytes("0\r\n\r\n");
        }

        var result = new List<byte>();
        var offset = 0;

        while (offset < data.Length)
        {
            var remainingBytes = data.Length - offset;
            var currentChunkSize = Math.Min(chunkSize, remainingBytes);

            // Write chunk size in hex
            var sizeHex = currentChunkSize.ToString("X");
            result.AddRange(Encoding.ASCII.GetBytes(sizeHex));
            result.Add(Cr);
            result.Add(Lf);

            // Write chunk data
            result.AddRange(data.Slice(offset, currentChunkSize).ToArray());
            result.Add(Cr);
            result.Add(Lf);

            offset += currentChunkSize;
        }

        // Write last chunk
        result.AddRange(Encoding.ASCII.GetBytes("0\r\n\r\n"));

        return result.ToArray();
    }
}
