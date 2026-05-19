using System.IO.Pipelines;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// A read-only <see cref="Stream"/> backed by a <see cref="PipeReader"/> with a content-length limit.
/// Used to stream large request bodies without buffering the entire payload.
/// </summary>
internal sealed class PipeReaderBodyStream : Stream
{
    private readonly PipeReader _reader;
    private long _remaining;

    public PipeReaderBodyStream(PipeReader reader, long contentLength)
    {
        _reader = reader;
        _remaining = contentLength;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0)
            return 0;

        var result = await _reader.ReadAsync(ct);
        var seq = result.Buffer;

        if (seq.IsEmpty)
        {
            _reader.AdvanceTo(seq.Start, seq.End);
            return result.IsCompleted ? 0 : await ReadAsync(buffer, ct);
        }

        var available = (int)Math.Min(Math.Min(seq.Length, (long)buffer.Length), _remaining);
        var slice = seq.Slice(0, available);
        // Copy each segment to the destination span
        int written = 0;
        foreach (var segment in slice)
        {
            segment.Span.CopyTo(buffer.Span.Slice(written));
            written += segment.Length;
        }
        _reader.AdvanceTo(slice.End);
        _remaining -= available;
        return available;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>Drain any unread bytes so the connection can handle the next request.</summary>
    public async ValueTask DrainAsync(CancellationToken ct = default)
    {
        while (_remaining > 0)
        {
            var result = await _reader.ReadAsync(ct);
            var seq = result.Buffer;
            var toConsume = Math.Min(seq.Length, _remaining);
            _reader.AdvanceTo(seq.Slice(0, toConsume).End, seq.End);
            _remaining -= toConsume;
            if (result.IsCompleted)
                break;
        }
    }
}
