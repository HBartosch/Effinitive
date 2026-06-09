using System.Buffers;
using System.IO.Pipelines;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Streaming dechunker: reads Transfer-Encoding: chunked data from a PipeReader
/// and exposes the raw payload as a Stream without ever buffering the full body.
/// Mirrors Kestrel's Http1ChunkedEncodingMessageBody state-machine approach.
/// </summary>
internal sealed class ChunkedBodyStream : Stream
{
    private readonly PipeReader _reader;
    private long _remainingInChunk;
    private bool _skipDataCrLf;
    private bool _finished;

    internal ChunkedBodyStream(PipeReader reader) => _reader = reader;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_finished || buffer.Length == 0)
            return 0;

        while (true)
        {
            var result = await _reader.ReadAsync(ct);
            var seq = result.Buffer;

            if (seq.IsEmpty)
            {
                _reader.AdvanceTo(seq.Start, seq.End);
                if (result.IsCompleted) return 0;
                continue;
            }

            var sr = new SequenceReader<byte>(seq);

            // Skip the CRLF that trails each chunk's data section
            if (_skipDataCrLf)
            {
                if (sr.Remaining < 2)
                {
                    _reader.AdvanceTo(sr.Position, seq.End);
                    continue;
                }
                // RFC 9112 §7.1: chunk data MUST be followed by CRLF
                sr.TryRead(out byte cr);
                sr.TryRead(out byte lf2);
                if (cr != (byte)'\r' || lf2 != (byte)'\n')
                    throw HttpParseException.BadRequest("Missing CRLF after chunk data");
                _skipDataCrLf = false;
            }

            // Parse chunk-size header lines until we enter a data chunk
            while (_remainingInChunk == 0 && !_finished)
            {
                long before = sr.Consumed;

                // Reject bare LF: if a \n appears before any \r the framing is invalid
                if (ContainsBareNewline(sr.UnreadSequence))
                    throw HttpParseException.BadRequest("Bare LF in chunked framing");

                // TryReadTo advances past the delimiter on success; leaves position unchanged on failure
                if (!sr.TryReadTo(out ReadOnlySpan<byte> sizeLine, (byte)'\r'))
                {
                    _reader.AdvanceTo(sr.Position, seq.End);
                    goto NextRead;
                }

                if (!sr.TryRead(out byte lf) || lf != (byte)'\n')
                {
                    // CR without LF — need more data; rewind to before the size line
                    sr.Rewind(sr.Consumed - before);
                    _reader.AdvanceTo(sr.Position, seq.End);
                    goto NextRead;
                }

                // Parse hex digits; reject OWS and validate extension
                ParseChunkSizeLine(sizeLine, out long chunkSize);

                if (chunkSize == 0)
                {
                    // Last chunk — consume trailers then declare EOF
                    if (!TrySkipTrailers(ref sr))
                    {
                        sr.Rewind(sr.Consumed - before);
                        _reader.AdvanceTo(sr.Position, seq.End);
                        goto NextRead;
                    }
                    _finished = true;
                    _reader.AdvanceTo(sr.Position);
                    return 0;
                }

                _remainingInChunk = chunkSize;
            }

            if (_finished) { _reader.AdvanceTo(sr.Position); return 0; }

            // Deliver chunk data to the caller
            if (sr.Remaining > 0)
            {
                var toRead = (int)Math.Min(Math.Min(sr.Remaining, _remainingInChunk), buffer.Length);
                sr.UnreadSequence.Slice(0, toRead).CopyTo(buffer.Span);
                sr.Advance(toRead);
                _remainingInChunk -= toRead;
                if (_remainingInChunk == 0) _skipDataCrLf = true;
                _reader.AdvanceTo(sr.Position);
                return toRead;
            }

            _reader.AdvanceTo(sr.Position, seq.End);
            continue;

        NextRead:;
        }
    }

    internal async ValueTask DrainAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        var buf = ArrayPool<byte>.Shared.Rent(65536);
        try { while (await ReadAsync(buf.AsMemory(0, 65536), ct) > 0) { } }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // Returns true if a bare \n (not preceded by \r) appears before any \r in the sequence
    private static bool ContainsBareNewline(ReadOnlySequence<byte> seq)
    {
        foreach (var segment in seq)
        {
            var span = segment.Span;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == (byte)'\r') return false; // \r comes first — no bare LF
                if (span[i] == (byte)'\n') return true;  // \n before any \r — bare LF
            }
        }
        return false;
    }

    // Parses "hexdigits[;ext]" per RFC 9112 §7.1.1. Throws HttpParseException on violation.
    private static void ParseChunkSizeLine(ReadOnlySpan<byte> line, out long chunkSize)
    {
        var semiIdx = line.IndexOf((byte)';');
        ReadOnlySpan<byte> hexPart;

        if (semiIdx >= 0)
        {
            hexPart = line.Slice(0, semiIdx);
            // Validate extension: must start with a valid token char (RFC 9110 §5.6.2 token)
            ValidateChunkExtension(line.Slice(semiIdx + 1));
        }
        else
        {
            hexPart = line;
        }

        if (!TryParseHex(hexPart, out chunkSize))
            throw HttpParseException.BadRequest("Invalid chunk size in chunked transfer encoding");
    }

    // chunk-ext = *( BWS ";" BWS chunk-ext-name [ "=" chunk-ext-val ] )
    // Validates that the extension name is a valid RFC 9110 token and the value has no control chars.
    private static void ValidateChunkExtension(ReadOnlySpan<byte> ext)
    {
        // Skip optional BWS before ext-name
        int i = 0;
        while (i < ext.Length && (ext[i] == ' ' || ext[i] == '\t')) i++;

        if (i >= ext.Length)
            throw HttpParseException.BadRequest("Bare semicolon in chunk extension");

        // chunk-ext-name = token — validate every char until '=', ';', BWS or end
        while (i < ext.Length && ext[i] != '=' && ext[i] != ';' && ext[i] != ' ' && ext[i] != '\t')
        {
            if (!IsTokenChar(ext[i]))
                throw HttpParseException.BadRequest("Invalid character in chunk extension name");
            i++;
        }

        // Scan remainder (ext-val or further extensions): reject control chars (< 0x20, except HTAB)
        for (; i < ext.Length; i++)
        {
            byte b = ext[i];
            if (b < 0x20 && b != (byte)'\t')
                throw HttpParseException.BadRequest("Control character in chunk extension");
        }
    }

    // Consume trailer lines (name: value\r\n) until the empty terminator line (\r\n)
    private static bool TrySkipTrailers(ref SequenceReader<byte> sr)
    {
        while (true)
        {
            long before = sr.Consumed;

            // Reject bare LF in trailers
            if (ContainsBareNewline(sr.UnreadSequence))
                throw HttpParseException.BadRequest("Bare LF in chunked trailers");

            if (!sr.TryReadTo(out ReadOnlySpan<byte> line, (byte)'\r'))
                return false;
            if (!sr.TryRead(out byte lf) || lf != (byte)'\n')
            {
                sr.Rewind(sr.Consumed - before);
                return false;
            }
            if (line.IsEmpty) return true; // empty line = end of trailers
        }
    }

    // RFC 9110 §5.6.2 token char: ALPHA / DIGIT / "!" / "#" / "$" / "%" / "&" / "'" /
    // "*" / "+" / "-" / "." / "^" / "_" / "`" / "|" / "~"
    private static bool IsTokenChar(byte b)
        => (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') ||
           b == '!' || b == '#' || b == '$' || b == '%' || b == '&' || b == '\'' ||
           b == '*' || b == '+' || b == '-' || b == '.' || b == '^' || b == '_' ||
           b == '`' || b == '|' || b == '~';

    // Strict hex parser — no leading/trailing whitespace allowed (RFC 9112 chunk-size = 1*HEXDIG)
    private static bool TryParseHex(ReadOnlySpan<byte> span, out long value)
    {
        value = 0;
        if (span.IsEmpty) return false;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            int d = b >= '0' && b <= '9' ? b - '0' :
                    b >= 'a' && b <= 'f' ? b - 'a' + 10 :
                    b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
            if (d < 0) return false;
            // Overflow check: chunk-size must fit in a long
            if (value > (long.MaxValue >> 4)) return false;
            value = (value << 4) | (uint)d;
        }
        return true;
    }
}
