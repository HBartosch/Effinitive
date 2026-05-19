using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Zero-allocation HTTP request parser using SequenceReader.
/// Validates against RFC 9110/9112 for correctness and security.
/// </summary>
public static class HttpRequestParser
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] HeaderSeparator = ": "u8.ToArray();
    private static readonly byte Space = (byte)' ';
    private static readonly byte Tab = (byte)'\t';
    private static readonly byte Cr = (byte)'\r';
    private static readonly byte Lf = (byte)'\n';
    private static readonly byte Colon = (byte)':';
    private static readonly byte Semicolon = (byte)';';

    // RFC limits
    private const int MaxMethodLength = 64;
    private const int MaxUrlLength = 8192;       // 8 KB
    private const int MaxHeaderNameLength = 8192;
    private const int MaxHeaderValueLength = 8192;
    private const int MaxHeaderCount = 100;
    private const int MaxTotalHeaderSize = 32768; // 32 KB

    /// <summary>
    /// Try to parse an HTTP request from the buffer.
    /// Throws HttpParseException for RFC violations.
    /// Returns false if more data is needed.
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

        // RFC 9112 §2.2: skip up to one leading CRLF (tolerate, but do not skip bare LF)
        if (reader.IsNext(Cr))
        {
            if (reader.Remaining < 2)
                return false;
            reader.Advance(1);
            if (reader.TryPeek(out byte nextAfterCr) && nextAfterCr == Lf)
            {
                reader.Advance(1);
                // Consumed one leading CRLF
            }
            else
            {
                // Bare CR — invalid
                throw HttpParseException.BadRequest("Bare CR in request (expected CRLF)");
            }
        }

        // Reject leading bare LF or whitespace
        if (reader.TryPeek(out byte firstByte))
        {
            if (firstByte == Lf)
                throw HttpParseException.BadRequest("Bare LF before request line");
            if (firstByte == Space || firstByte == Tab)
                throw HttpParseException.BadRequest("Whitespace before request line");
        }

        // Parse request line: METHOD SP request-target SP HTTP-version CRLF
        if (!TryParseRequestLine(ref reader, request))
        {
            return false;
        }

        // Parse headers until \r\n\r\n
        if (!TryParseHeaders(ref reader, request))
        {
            return false;
        }

        // Post-header validation
        ValidateHeaders(request);

        // Determine body handling based on Transfer-Encoding / Content-Length
        var hasTE = request.Headers.TryGetValue("Transfer-Encoding", out var te);
        var hasCL = request.ContentLength >= 0;

        if (hasTE && hasCL)
        {
            // RFC 9112 §6.1: A server MUST treat receiving both as an error
            // in request messages to avoid smuggling
            throw HttpParseException.BadRequest("Request contains both Transfer-Encoding and Content-Length");
        }

        if (hasTE)
        {
            // Validate the TE value is exactly "chunked"
            ValidateTransferEncoding(te!);

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

            request.Body = body.AsMemory();
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

            // Large bodies (> 1 MB) are streamed to avoid large heap allocations.
            // The connection handler attaches a PipeReaderBodyStream for the endpoint.
            const int StreamingThreshold = 1 * 1024 * 1024; // 1 MB
            if (request.ContentLength > StreamingThreshold)
            {
                // Return immediately — body remains in the pipe for streaming
                consumed = reader.Position;
                bytesConsumed = (int)reader.Consumed;
                request.BodyDeferred = true;
                return true;
            }

            if (reader.Remaining < request.ContentLength)
            {
                return false; // Need more data
            }

            // Read body into a pooled buffer to avoid per-request heap allocations
            var rentedBuffer = ArrayPool<byte>.Shared.Rent((int)request.ContentLength);
            reader.UnreadSequence.Slice(0, request.ContentLength).CopyTo(rentedBuffer);
            request.RentedBodyBuffer = rentedBuffer;
            request.Body = new ReadOnlyMemory<byte>(rentedBuffer, 0, (int)request.ContentLength);
            reader.Advance(request.ContentLength);
        }

        consumed = reader.Position;
        bytesConsumed = (int)reader.Consumed;
        return true;
    }

    private static bool TryParseRequestLine(ref SequenceReader<byte> reader, HttpRequest request)
    {
        // We need to find the full request line ending in CRLF
        // Scan forward to find CR+LF
        var startPosition = reader.Position;
        long startConsumed = reader.Consumed;

        if (!reader.TryReadTo(out ReadOnlySpan<byte> requestLineBytes, Cr))
        {
            return false; // Need more data
        }

        // Must be followed by LF
        if (!reader.TryRead(out byte lf))
            return false;
        if (lf != Lf)
            throw HttpParseException.BadRequest("Invalid line ending in request line (expected CRLF)");

        // Now parse the request line from the bytes we read
        // Format: METHOD SP request-target SP HTTP-version
        // Reject any bare LF, bare CR, NUL, or control chars in the line
        ValidateRequestLineBytes(requestLineBytes);

        // Find first SP — method
        int firstSpace = requestLineBytes.IndexOf(Space);
        if (firstSpace < 0)
            throw HttpParseException.BadRequest("Missing SP in request line");
        if (firstSpace == 0)
            throw HttpParseException.BadRequest("Empty method in request line");

        var methodBytes = requestLineBytes.Slice(0, firstSpace);
        if (methodBytes.Length > MaxMethodLength)
            throw HttpParseException.BadRequest("Method too long");

        // Validate method is all tokens (visible ASCII minus delimiters)
        ValidateMethodToken(methodBytes);
        request.Method = Encoding.ASCII.GetString(methodBytes);

        // Find last SP — HTTP-version comes after it
        int lastSpace = requestLineBytes.LastIndexOf(Space);
        if (lastSpace == firstSpace)
            throw HttpParseException.BadRequest("Missing request-target or HTTP-version in request line");

        // Check for multiple spaces between method and target, or target and version
        var targetBytes = requestLineBytes.Slice(firstSpace + 1, lastSpace - firstSpace - 1);
        if (targetBytes.Length == 0)
            throw HttpParseException.BadRequest("Empty request-target");
        if (targetBytes.Length > MaxUrlLength)
            throw HttpParseException.UriTooLong("Request-URI too long");

        // Validate the target has no spaces (which would indicate extra SPs in request line)
        if (targetBytes.IndexOf(Space) >= 0)
            throw HttpParseException.BadRequest("Invalid request-target (contains space)");

        // Validate no NUL or control chars in target
        ValidateTargetBytes(targetBytes);

        var targetStr = Encoding.ASCII.GetString(targetBytes);

        // Reject dangerous percent-encoded sequences in URL
        ValidatePercentEncoding(targetStr);

        request.Path = targetStr;

        // Parse HTTP-version
        var versionBytes = requestLineBytes.Slice(lastSpace + 1);
        var versionStr = Encoding.ASCII.GetString(versionBytes);
        ValidateHttpVersion(versionStr);
        request.HttpVersion = versionStr;

        // RFC 9112 §3.2: Reject fragments in request target
        var fragmentIdx = request.Path.IndexOf('#');
        if (fragmentIdx >= 0)
        {
            throw HttpParseException.BadRequest("Fragment identifier in request-target is not allowed");
        }

        // Handle absolute-form URI (http://host/path)
        if (request.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Extract host from absolute URI for Host header comparison
            var uriStart = request.Path.IndexOf("://") + 3;
            var pathStart = request.Path.IndexOf('/', uriStart);
            var absoluteHost = pathStart >= 0
                ? request.Path.Substring(uriStart, pathStart - uriStart)
                : request.Path.Substring(uriStart);
            request.Items ??= new Dictionary<string, object>();
            request.Items["AbsoluteFormHost"] = absoluteHost;

            // Extract path component from absolute URI
            request.Path = pathStart >= 0 ? request.Path.Substring(pathStart) : "/";
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateRequestLineBytes(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == Lf)
                throw HttpParseException.BadRequest("Bare LF in request line");
            if (b == 0)
                throw HttpParseException.BadRequest("NUL byte in request line");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateMethodToken(ReadOnlySpan<byte> method)
    {
        // RFC 9110 §5.6.2: token = 1*tchar
        // tchar = "!" / "#" / "$" / "%" / "&" / "'" / "*" / "+" / "-" / "." /
        //         "^" / "_" / "`" / "|" / "~" / DIGIT / ALPHA
        for (int i = 0; i < method.Length; i++)
        {
            byte b = method[i];
            if (!IsTokenChar(b))
                throw HttpParseException.BadRequest($"Invalid character in method: 0x{b:X2}");
            // RFC 9110 §9.1: Methods are case-sensitive. Standard methods are all uppercase.
            // Reject lowercase letters to prevent method confusion attacks.
            if (b >= (byte)'a' && b <= (byte)'z')
                throw HttpParseException.NotImplemented($"Method must be uppercase (got lowercase character)");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateTargetBytes(ReadOnlySpan<byte> target)
    {
        for (int i = 0; i < target.Length; i++)
        {
            byte b = target[i];
            if (b == 0) // NUL
                throw HttpParseException.BadRequest("NUL byte in request target");
            if (b == (byte)'\\') // Backslash
                throw HttpParseException.BadRequest("Backslash in request target");
            if (b < 0x21 && b != Tab) // Control chars except tab (which some servers allow)
                throw HttpParseException.BadRequest($"Control character in request target: 0x{b:X2}");
            if (b > 0x7E) // Non-ASCII
                throw HttpParseException.BadRequest($"Non-ASCII byte in request target: 0x{b:X2}");
        }
    }

    private static void ValidatePercentEncoding(string target)
    {
        // Reject dangerous percent-encoded sequences
        for (int i = 0; i < target.Length - 2; i++)
        {
            if (target[i] != '%') continue;
            char h1 = char.ToLowerInvariant(target[i + 1]);
            char h2 = char.ToLowerInvariant(target[i + 2]);
            // %00 = NUL
            if (h1 == '0' && h2 == '0')
                throw HttpParseException.BadRequest("Percent-encoded NUL (%00) in request target");
            // %0d = CR
            if (h1 == '0' && h2 == 'd')
                throw HttpParseException.BadRequest("Percent-encoded CR (%0d) in request target");
            // %0a = LF
            if (h1 == '0' && h2 == 'a')
                throw HttpParseException.BadRequest("Percent-encoded LF (%0a) in request target");
        }
    }

    private static void ValidateHttpVersion(string version)
    {
        // RFC 9112 §2.3: HTTP-version = HTTP-name "/" DIGIT "." DIGIT
        // HTTP-name = "HTTP" (case sensitive!)
        if (version.Length < 8)
            throw HttpParseException.BadRequest($"Invalid HTTP-version: {version}");

        if (!version.StartsWith("HTTP/", StringComparison.Ordinal))
            throw HttpParseException.BadRequest($"Invalid HTTP-version prefix: {version}");

        // Must be "HTTP/" DIGIT "." DIGIT exactly
        if (version.Length != 8)
            throw HttpParseException.BadRequest($"Invalid HTTP-version length: {version}");

        char major = version[5];
        char dot = version[6];
        char minor = version[7];

        if (dot != '.' || !char.IsAsciiDigit(major) || !char.IsAsciiDigit(minor))
            throw HttpParseException.BadRequest($"Invalid HTTP-version format: {version}");

        // Only support HTTP/1.x (server handles version dispatching)
        if (major != '1')
            throw HttpParseException.VersionNotSupported($"Unsupported HTTP version: {version}");
    }

    private static bool TryParseHeaders(ref SequenceReader<byte> reader, HttpRequest request)
    {
        int headerCount = 0;
        long totalHeaderBytes = 0;

        while (true)
        {
            // Check for end of headers (\r\n)
            if (!reader.TryPeek(out byte peekByte))
                return false; // Need more data

            if (peekByte == Cr)
            {
                reader.Advance(1);
                if (!reader.TryRead(out byte lfByte))
                    return false;
                if (lfByte != Lf)
                    throw HttpParseException.BadRequest("Expected CRLF at end of headers");
                break; // End of headers
            }

            // RFC 9112 §5.1: reject obs-fold (line starts with SP or HTAB)
            if (peekByte == Space || peekByte == Tab)
                throw HttpParseException.BadRequest("Obsolete line folding (obs-fold) in headers");

            // Reject bare LF
            if (peekByte == Lf)
                throw HttpParseException.BadRequest("Bare LF in headers");

            // Header limit check
            headerCount++;
            if (headerCount > MaxHeaderCount)
                throw HttpParseException.HeaderFieldsTooLarge("Too many headers");

            // Read header line up to CR
            if (!reader.TryReadTo(out ReadOnlySpan<byte> headerLineBytes, Cr))
                return false;

            // Must be followed by LF
            if (!reader.TryRead(out byte lf) || lf != Lf)
                throw HttpParseException.BadRequest("Invalid header line ending (expected CRLF)");

            totalHeaderBytes += headerLineBytes.Length + 2; // +2 for CRLF
            if (totalHeaderBytes > MaxTotalHeaderSize)
                throw HttpParseException.HeaderFieldsTooLarge("Total header size exceeds limit");

            // Find the colon separator
            int colonIdx = headerLineBytes.IndexOf(Colon);
            if (colonIdx < 0)
                throw HttpParseException.BadRequest("Header line missing colon separator");
            if (colonIdx == 0)
                throw HttpParseException.BadRequest("Empty header name");

            var nameBytes = headerLineBytes.Slice(0, colonIdx);

            // RFC 9110 §5.6.2: no whitespace between field-name and colon
            byte lastNameByte = nameBytes[nameBytes.Length - 1];
            if (lastNameByte == Space || lastNameByte == Tab)
                throw HttpParseException.BadRequest("Space before colon in header name");

            // Validate header name is all token chars
            if (nameBytes.Length > MaxHeaderNameLength)
                throw HttpParseException.HeaderFieldsTooLarge("Header name too long");
            ValidateHeaderName(nameBytes);

            // Value starts after colon, capture raw then trim OWS
            var rawValueBytes = headerLineBytes.Slice(colonIdx + 1);
            var valueBytes = TrimOWS(rawValueBytes);

            if (valueBytes.Length > MaxHeaderValueLength)
                throw HttpParseException.HeaderFieldsTooLarge("Header value too long");

            // Validate header value (no NUL, no bare CR/LF, no most control chars)
            ValidateHeaderValue(valueBytes);

            var name = Encoding.ASCII.GetString(nameBytes);
            var value = Encoding.ASCII.GetString(valueBytes);

            // Reject headers with underscores in name — prevents smuggling via
            // underscore variants like Content_Length or Transfer_Encoding
            // (same behavior as nginx default underscore_in_headers=off)
            if (name.Contains('_'))
                throw HttpParseException.BadRequest($"Underscore in header name: {name}");

            // Handle duplicate headers
            if (request.Headers.TryGetValue(name, out var existingValue))
            {
                // RFC 9110 §5.3: multiple values are combined with comma
                // Exception: special handling for Content-Length and Host
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    // Duplicate Content-Length: reject if different values (smuggling vector)
                    if (!existingValue.Equals(value, StringComparison.Ordinal))
                        throw HttpParseException.BadRequest("Duplicate Content-Length headers with different values");
                    // Same value is ok, don't update
                    continue;
                }
                else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    // RFC 9112 §7.1: A server MUST respond with 400 to duplicate Host
                    throw HttpParseException.BadRequest("Duplicate Host header");
                }
                else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    // Duplicate Content-Type is a smuggling vector
                    throw HttpParseException.BadRequest("Duplicate Content-Type header");
                }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    // Combine TE values with comma
                    request.Headers[name] = existingValue + ", " + value;
                }
                else
                {
                    // Combine with comma for other headers
                    request.Headers[name] = existingValue + ", " + value;
                }
            }
            else
            {
                request.Headers[name] = value;
            }

            // Parse important headers
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                // Reject Content-Length values with raw whitespace (smuggling vector)
                // Standard OWS (one SP after colon) is fine, but extra/tab is suspicious
                ValidateContentLengthRaw(rawValueBytes);
                ValidateContentLength(value);
                request.ContentLength = long.Parse(value);
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                request.KeepAlive = !value.Equals("close", StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    private static void ValidateHeaders(HttpRequest request)
    {
        // RFC 9112 §7.1: HTTP/1.1 requests MUST have Host header
        if (request.HttpVersion == "HTTP/1.1")
        {
            if (!request.Headers.ContainsKey("Host"))
                throw HttpParseException.BadRequest("Missing Host header (required in HTTP/1.1)");

            var host = request.Headers["Host"];

            // Empty Host value is invalid per RFC 9112 §7.1 unless target is in absolute-form
            // (which we've already handled by extracting path)

            // Validate Host header value
            ValidateHostHeader(host);
        }
    }

    private static void ValidateHostHeader(string host)
    {
        if (string.IsNullOrEmpty(host))
            throw HttpParseException.BadRequest("Empty Host header value");

        // Host MUST NOT contain comma (multiple hosts in one value)
        if (host.Contains(','))
            throw HttpParseException.BadRequest("Host header contains multiple values (comma)");

        // Host MUST NOT contain userinfo (@ sign)
        if (host.Contains('@'))
            throw HttpParseException.BadRequest("Host header contains userinfo (@)");

        // Host MUST NOT contain a path (/)
        // But host:port is fine, so check for / not part of port
        var slashIdx = host.IndexOf('/');
        if (slashIdx >= 0)
            throw HttpParseException.BadRequest("Host header contains path (/)");
    }

    /// <summary>
    /// Validate the raw Content-Length value bytes BEFORE OWS trimming.
    /// Rejects values where tabs, extra leading/trailing whitespace could cause
    /// different parsing between proxies and the server (smuggling vector).
    /// Allows at most one leading SP (standard ": value" form) and no trailing SP.
    /// </summary>
    private static void ValidateContentLengthRaw(ReadOnlySpan<byte> rawValue)
    {
        if (rawValue.Length == 0) return;

        // Reject any HTAB characters anywhere in the raw CL value
        for (int i = 0; i < rawValue.Length; i++)
        {
            if (rawValue[i] == (byte)'\t')
                throw HttpParseException.BadRequest("Content-Length value contains tab character");
        }

        // Reject trailing whitespace
        if (rawValue[rawValue.Length - 1] == (byte)' ')
            throw HttpParseException.BadRequest("Content-Length value has trailing whitespace");

        // Allow at most 1 leading SP (the standard OWS after colon), reject more
        int leadingSp = 0;
        for (int i = 0; i < rawValue.Length; i++)
        {
            if (rawValue[i] == (byte)' ') leadingSp++;
            else break;
        }
        if (leadingSp > 1)
            throw HttpParseException.BadRequest("Content-Length value has extra leading whitespace");
    }

    private static void ValidateContentLength(string value)
    {
        // RFC 9110 §8.6: Content-Length = 1*DIGIT
        // Must be a non-negative integer with no leading zeros or signs
        if (string.IsNullOrEmpty(value))
            throw HttpParseException.BadRequest("Empty Content-Length value");

        // Check every character is a digit
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsAsciiDigit(c))
                throw HttpParseException.BadRequest($"Non-numeric character in Content-Length: '{c}'");
        }

        // Reject leading zeros (except "0" itself)
        if (value.Length > 1 && value[0] == '0')
            throw HttpParseException.BadRequest("Content-Length has leading zeros");

        // Check for overflow
        if (!long.TryParse(value, out var cl) || cl < 0)
            throw HttpParseException.BadRequest($"Invalid Content-Length value: {value}");
    }

    private static void ValidateTransferEncoding(string te)
    {
        // RFC 9112 §6.1: only "chunked" is supported in requests
        // Reject any obfuscated form: trailing spaces, case issues are OK, but
        // reject CL+TE conflicts, non-chunked values, etc.
        var trimmed = te.Trim();

        // Check for multiple values (comma-separated)
        if (trimmed.Contains(','))
            throw HttpParseException.BadRequest("Multiple Transfer-Encoding values not supported");

        // Must be exactly "chunked" (case-insensitive)
        if (!trimmed.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            throw HttpParseException.BadRequest($"Unsupported Transfer-Encoding: {trimmed}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateHeaderName(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            byte b = name[i];
            if (!IsTokenChar(b))
                throw HttpParseException.BadRequest($"Invalid character in header name: 0x{b:X2}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateHeaderValue(ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            byte b = value[i];
            if (b == 0) // NUL
                throw HttpParseException.BadRequest("NUL byte in header value");
            if (b == Cr || b == Lf) // Bare CR or LF in value
                throw HttpParseException.BadRequest("Bare CR/LF in header value");
            // Control chars 0x01-0x08, 0x0B, 0x0C, 0x0E-0x1F, 0x7F are invalid
            if (b < 0x20 && b != Tab) // Tab (0x09) is allowed as OWS
                throw HttpParseException.BadRequest($"Control character in header value: 0x{b:X2}");
            if (b == 0x7F) // DEL
                throw HttpParseException.BadRequest("DEL character in header value");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTokenChar(byte b)
    {
        // RFC 9110 §5.6.2: tchar = "!" / "#" / "$" / "%" / "&" / "'" / "*" /
        //  "+" / "-" / "." / "^" / "_" / "`" / "|" / "~" / DIGIT / ALPHA
        if (b >= 'A' && b <= 'Z') return true;
        if (b >= 'a' && b <= 'z') return true;
        if (b >= '0' && b <= '9') return true;
        return b switch
        {
            (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or
            (byte)'\'' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.' or
            (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~' => true,
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimOWS(ReadOnlySpan<byte> value)
    {
        // Trim leading and trailing optional whitespace (SP or HTAB)
        int start = 0;
        while (start < value.Length && (value[start] == Space || value[start] == Tab))
            start++;

        int end = value.Length - 1;
        while (end >= start && (value[end] == Space || value[end] == Tab))
            end--;

        return value.Slice(start, end - start + 1);
    }
}
