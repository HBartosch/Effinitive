using System.Buffers;
using System.Text;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Chunked transfer encoding parser for HTTP/1.1 (RFC 7230 §4.1)
/// Handles chunked request/response bodies
/// </summary>
public static class ChunkedEncodingParser
{
    private static readonly byte Cr = (byte)'\r';
    private static readonly byte Lf = (byte)'\n';

    /// <summary>
    /// Parse chunked transfer encoding from buffer
    /// Returns true if complete, false if more data needed
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
            // Read chunk size (hex number until \r\n)
            if (!reader.TryReadTo(out ReadOnlySpan<byte> chunkSizeBytes, Cr))
            {
                return false; // Need more data
            }

            // Consume \n
            if (!reader.TryRead(out byte lf) || lf != Lf)
            {
                throw new InvalidOperationException("Invalid chunked encoding: expected LF after chunk size");
            }

            // Parse chunk size (hex)
            var chunkSizeStr = Encoding.ASCII.GetString(chunkSizeBytes);
            
            // Handle chunk extensions (;name=value) - ignore them
            var semicolonIndex = chunkSizeStr.IndexOf(';');
            if (semicolonIndex >= 0)
            {
                chunkSizeStr = chunkSizeStr.Substring(0, semicolonIndex);
            }

            if (!int.TryParse(chunkSizeStr.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
            {
                throw new InvalidOperationException($"Invalid chunk size: {chunkSizeStr}");
            }

            // Last chunk (size = 0)
            if (chunkSize == 0)
            {
                // Read trailer headers (until \r\n\r\n)
                // For simplicity, skip them for now
                while (true)
                {
                    if (reader.IsNext(Cr))
                    {
                        reader.Advance(1);
                        if (reader.TryRead(out byte lfByte) && lfByte == Lf)
                        {
                            // End of trailers
                            break;
                        }
                    }
                    
                    // Skip trailer line
                    if (!reader.TryReadTo(out ReadOnlySpan<byte> _, Cr))
                    {
                        return false; // Need more data for trailers
                    }
                    
                    if (!reader.TryRead(out lf) || lf != Lf)
                    {
                        throw new InvalidOperationException("Invalid chunked encoding: expected LF in trailer");
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

            // Read \r\n after chunk data
            if (!reader.TryRead(out byte cr) || cr != Cr)
            {
                throw new InvalidOperationException("Invalid chunked encoding: expected CR after chunk data");
            }

            if (!reader.TryRead(out lf) || lf != Lf)
            {
                throw new InvalidOperationException("Invalid chunked encoding: expected LF after chunk data");
            }
        }
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
