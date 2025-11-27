using System.Buffers;
using System.Text;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Zero-allocation HTTP request parser using SequenceReader
/// </summary>
public static class HttpRequestParser
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] HeaderSeparator = ": "u8.ToArray();
    private static readonly byte Space = (byte)' ';
    private static readonly byte Cr = (byte)'\r';
    private static readonly byte Lf = (byte)'\n';
    private static readonly byte Colon = (byte)':';

    /// <summary>
    /// Try to parse an HTTP request from the buffer
    /// </summary>
    public static bool TryParseRequest(
        ref ReadOnlySequence<byte> buffer,
        HttpRequest request,
        out SequencePosition consumed,
        out int bytesConsumed,
        int maxBodySize = 30 * 1024 * 1024) // Default 30MB
    {
        consumed = buffer.Start;
        bytesConsumed = 0;
        request.Reset();

        var reader = new SequenceReader<byte>(buffer);

        // Parse request line: METHOD /path HTTP/1.1\r\n
        if (!TryParseRequestLine(ref reader, request))
        {
            return false;
        }

        // Parse headers until \r\n\r\n
        if (!TryParseHeaders(ref reader, request))
        {
            return false;
        }

        // Check if we need to read body based on Content-Length or Transfer-Encoding
        var transferEncoding = request.Headers.TryGetValue("Transfer-Encoding", out var te) ? te : null;
        
        if (transferEncoding?.Equals("chunked", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Parse chunked encoding
            var remainingBuffer = buffer.Slice(reader.Position);
            if (!ChunkedEncodingParser.TryParseChunked(
                ref remainingBuffer,
                out var body,
                out var chunkConsumed,
                out var chunkBytesConsumed,
                maxBodySize))
            {
                return false; // Need more data
            }
            
            request.Body = body;
            request.ContentLength = body.Length;
            reader.Advance(chunkBytesConsumed);
        }
        else if (request.ContentLength > 0)
        {
            // SECURITY: Validate body size to prevent DoS via unbounded allocation
            if (request.ContentLength > maxBodySize)
            {
                throw new InvalidOperationException($"Request body size {request.ContentLength} exceeds maximum allowed size {maxBodySize}");
            }
            
            if (reader.Remaining < request.ContentLength)
            {
                return false; // Need more data
            }

            // Read body into byte array
            var bodyBytes = new byte[request.ContentLength];
            reader.UnreadSequence.Slice(0, request.ContentLength).CopyTo(bodyBytes);
            request.Body = bodyBytes;
            reader.Advance(request.ContentLength);
        }

        consumed = reader.Position;
        bytesConsumed = (int)reader.Consumed;
        return true;
    }

    private static bool TryParseRequestLine(ref SequenceReader<byte> reader, HttpRequest request)
    {
        // Read until first space (METHOD)
        if (!reader.TryReadTo(out ReadOnlySpan<byte> methodBytes, Space))
        {
            return false;
        }
        request.Method = Encoding.ASCII.GetString(methodBytes);

        // Read until second space (PATH)
        if (!reader.TryReadTo(out ReadOnlySpan<byte> pathBytes, Space))
        {
            return false;
        }
        request.Path = Encoding.ASCII.GetString(pathBytes);

        // Read until \r\n (HTTP VERSION)
        if (!reader.TryReadTo(out ReadOnlySpan<byte> versionBytes, Cr))
        {
            return false;
        }
        
        // Consume \n
        if (!reader.TryRead(out byte lf) || lf != Lf)
        {
            return false;
        }

        request.HttpVersion = Encoding.ASCII.GetString(versionBytes);
        return true;
    }

    private static bool TryParseHeaders(ref SequenceReader<byte> reader, HttpRequest request)
    {
        while (true)
        {
            // Check for end of headers (\r\n)
            if (reader.IsNext(Cr))
            {
                reader.Advance(1);
                if (reader.TryRead(out byte lfByte) && lfByte == Lf)
                {
                    // End of headers
                    break;
                }
                return false;
            }

            // Read header name (until :)
            if (!reader.TryReadTo(out ReadOnlySpan<byte> nameBytes, Colon))
            {
                return false;
            }

            // Skip space after colon
            if (reader.TryPeek(out byte next) && next == Space)
            {
                reader.Advance(1);
            }

            // Read header value (until \r\n)
            if (!reader.TryReadTo(out ReadOnlySpan<byte> valueBytes, Cr))
            {
                return false;
            }

            // Consume \n
            if (!reader.TryRead(out byte lf) || lf != Lf)
            {
                return false;
            }

            var name = Encoding.ASCII.GetString(nameBytes);
            var value = Encoding.ASCII.GetString(valueBytes);
            request.Headers[name] = value;

            // Parse important headers
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(value, out var contentLength))
                {
                    request.ContentLength = contentLength;
                }
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                request.KeepAlive = !value.Equals("close", StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }
}
