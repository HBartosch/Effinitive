using System.Buffers;
using System.Net.Quic;

namespace EffinitiveFramework.Core.Http3;

/// <summary>
/// A buffered view over one QUIC stream, for reading HTTP/3 frames.
/// </summary>
/// <remarks>
/// <para>
/// Reading a frame straight from the stream costs a read per field. A variable-length integer
/// (RFC 9000 §16) needs its first byte before its length is known, so the frame type alone is one
/// read for the prefix and another for the rest, then the same again for the frame length, then
/// one more for the payload. A request that transfers a couple of hundred bytes therefore suspends
/// three to five times before it has even been parsed.
/// </para>
/// <para>
/// Each suspension is a round trip into the transport that the request waits on, which is why a
/// server built that way sits idle with cores to spare: it is bound by how often it waits, not by
/// how much it computes. Filling a buffer once and parsing from memory turns the common request
/// into a single wait.
/// </para>
/// </remarks>
internal sealed class Http3FrameReader : IDisposable
{
    // Large enough for a request's frame headers and a typical field section in one read, small
    // enough that a connection's worth of concurrent streams does not hold much.
    private const int InitialSize = 2048;

    private readonly QuicStream _stream;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public Http3FrameReader(QuicStream stream)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialSize);
    }

    private int Buffered => _end - _start;

    /// <summary>
    /// Reads until at least <paramref name="count"/> bytes are available, or the peer stops sending.
    /// </summary>
    public async ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
    {
        if (Buffered >= count)
            return true;

        MakeRoomFor(count);

        while (Buffered < count)
        {
            int read = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken);
            if (read == 0)
                return false;

            _end += read;
        }

        return true;
    }

    /// <summary>
    /// Reads one variable-length integer (RFC 9000 §16), or -1 if the stream ended first.
    /// </summary>
    public async ValueTask<long> ReadVariableIntAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureAsync(1, cancellationToken))
            return -1;

        // The top two bits give the encoded length, so the first byte has to be in hand before
        // the rest can be asked for. Having it buffered is what keeps that from being a second read.
        int length = 1 << (_buffer[_start] >> 6);
        if (!await EnsureAsync(length, cancellationToken))
            return -1;

        long value = _buffer[_start] & 0x3F;
        for (int i = 1; i < length; i++)
            value = (value << 8) | _buffer[_start + i];

        _start += length;
        return value;
    }

    /// <summary>The buffered bytes, valid until the next read.</summary>
    public ReadOnlySpan<byte> Peek(int count) => _buffer.AsSpan(_start, count);

    public void Advance(int count) => _start += count;

    /// <summary>Whether anything is still buffered from an earlier read.</summary>
    public bool HasBuffered => Buffered > 0;

    private void MakeRoomFor(int count)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, Buffered);
            _end -= _start;
            _start = 0;
        }

        if (_buffer.Length >= count)
            return;

        var larger = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(_buffer, 0, larger, 0, _end);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    /// <summary>
    /// Reads to the end of the stream, so that closing it is graceful rather than an abort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QuicStream.DisposeAsync aborts the read side if it has not completed, which puts a
    /// STOP_SENDING frame on the wire and makes the close wait on the peer acknowledging it. The
    /// receive side only completes on a read that reaches the end, so a reader that stops as soon
    /// as it has the bytes it wanted leaves every stream to be torn down the expensive way.
    /// </para>
    /// <para>
    /// Only attempted once the peer has finished sending, so the read returns at once and nothing
    /// waits on a client that has more to say. A stream abandoned mid-request is still aborted,
    /// which is what abandoning it means.
    /// </para>
    /// </remarks>
    public async ValueTask DrainToEndAsync(CancellationToken cancellationToken)
    {
        if (!_stream.ReadsClosed.IsCompletedSuccessfully)
            return;

        _start = 0;
        _end = 0;

        while (await _stream.ReadAsync(_buffer, cancellationToken) > 0)
        {
        }
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
