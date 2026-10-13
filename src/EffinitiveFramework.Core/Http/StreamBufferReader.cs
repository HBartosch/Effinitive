using System.Buffers;
using System.IO.Pipelines;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// A <see cref="PipeReader"/> over a stream that buffers into one contiguous array and does no
/// locking.
/// </summary>
/// <remarks>
/// <para>
/// Plaintext HTTP/1.1 reads through the socket transport: pooled event args, an I/O queue, a
/// zero-byte receive while idle. TLS cannot use that, because <see cref="System.Net.Security.SslStream"/>
/// is a stream, so it fell back to <c>PipeReader.Create(stream, …)</c>. That reader is built for
/// a producer and a consumer on different threads and synchronises every read accordingly; a
/// profile of the TLS JSON workload put 21.4% of the connection's read path inside
/// <c>System.Threading.Lock.EnterAndGetCurrentCount</c>, with the machine only a third busy.
/// </para>
/// <para>
/// A connection reads its own requests one after another, so there is no second thread to
/// synchronise with and nothing for a lock to protect. Holding the bytes in a single array also
/// means a read result is one segment rather than a chain, so the parser sees a contiguous span
/// and no segment objects are allocated per read.
/// </para>
/// <para>
/// This changes nothing on the wire. RFC 9112 §2.1 defines the shape of a message; how an
/// implementation buffers octets between the TLS record layer and its parser is outside what
/// the specification describes.
/// </para>
/// </remarks>
internal sealed class StreamBufferReader : PipeReader
{
    private readonly Stream _stream;
    private readonly int _minimumReadSpace;

    private byte[] _buffer;
    private int _start;          // first octet the consumer has not consumed
    private int _end;            // one past the last octet read from the stream
    private bool _endOfStream;
    private bool _completed;
    private bool _cancelNextRead;

    // Set when the consumer says it examined everything it was given. Until then a second
    // ReadAsync must not block: the consumer has not yet looked at what it already holds.
    private bool _needMoreData = true;

    private ReadOnlySequence<byte> _lastReturned;

    internal StreamBufferReader(Stream stream, int bufferSize = 65536, int minimumReadSpace = 4096)
    {
        _stream = stream;
        _minimumReadSpace = minimumReadSpace;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    private ReadOnlySequence<byte> Buffered()
    {
        _lastReturned = new ReadOnlySequence<byte>(_buffer, _start, _end - _start);
        return _lastReturned;
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        while (true)
        {
            if (_cancelNextRead)
            {
                _cancelNextRead = false;
                return new ReadResult(Buffered(), isCanceled: true, isCompleted: false);
            }

            // Hand back what is already held unless the consumer has seen all of it, and
            // always hand it back once the stream has ended so the tail is not stranded.
            if ((!_needMoreData && _end > _start) || _endOfStream)
                return new ReadResult(Buffered(), isCanceled: false, isCompleted: _endOfStream);

            MakeRoom();

            int read = await _stream.ReadAsync(
                _buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken).ConfigureAwait(false);

            if (read == 0)
                _endOfStream = true;
            else
                _end += read;

            _needMoreData = false;
        }
    }

    public override bool TryRead(out ReadResult result)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        if ((!_needMoreData && _end > _start) || _endOfStream)
        {
            result = new ReadResult(Buffered(), isCanceled: false, isCompleted: _endOfStream);
            return true;
        }

        result = default;
        return false;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        // Measured by slicing rather than with GetOffset. On a sequence built over an array
        // segment, GetOffset returns the index into the array, not the distance from the start
        // of the sequence -- so once the window had moved off zero it over-reported, this
        // advanced past the end, and unread octets were dropped. Slicing is defined in terms of
        // the sequence itself and cannot drift from it.
        long consumedOffset = _lastReturned.Length - _lastReturned.Slice(consumed).Length;
        long examinedOffset = _lastReturned.Length - _lastReturned.Slice(examined).Length;

        // Examining everything on offer is the consumer saying it cannot make progress without
        // more octets, which is the only thing that makes the next read block.
        _needMoreData = examinedOffset >= _lastReturned.Length;

        if (consumedOffset < 0 || consumedOffset > _lastReturned.Length)
            throw new InvalidOperationException(
                $"Advanced to {consumedOffset} in a buffer of {_lastReturned.Length} octets.");

        _start += (int)consumedOffset;

        // Drained: start again at the front rather than letting the window walk down the array.
        if (_start >= _end)
        {
            _start = 0;
            _end = 0;
        }
    }

    /// <summary>Make space for the next read, by sliding the unconsumed octets or by growing.</summary>
    private void MakeRoom()
    {
        if (_buffer.Length - _end >= _minimumReadSpace)
            return;

        int held = _end - _start;

        if (_start > 0)
        {
            // Slide what is unconsumed back to the front. A pipelined connection reaches here
            // constantly, because each request consumes a prefix and leaves the next behind.
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, held);
            _start = 0;
            _end = held;

            if (_buffer.Length - _end >= _minimumReadSpace)
                return;
        }

        // Still short, so the unconsumed octets alone fill the array: one message is larger than
        // the buffer. Growing is bounded in practice because the parser refuses a body beyond
        // the configured maximum and fails the request before this can run away.
        var larger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
        Buffer.BlockCopy(_buffer, 0, larger, 0, _end);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    public override void CancelPendingRead() => _cancelNextRead = true;

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
            return;

        _completed = true;
        _lastReturned = default;

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];

        // The stream is owned by the connection, which disposes it alongside the socket.
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        Complete(exception);
        return ValueTask.CompletedTask;
    }
}
