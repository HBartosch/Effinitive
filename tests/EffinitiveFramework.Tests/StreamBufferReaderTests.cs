using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// The buffered reader the TLS HTTP/1.1 path uses in place of <c>PipeReader.Create</c>.
/// </summary>
/// <remarks>
/// It replaces a reader built for a producer and a consumer on separate threads, which
/// synchronises every read for a connection that has only one reader. What it must not change
/// is the contract the parser relies on: octets arrive in order and none are lost, consuming a
/// prefix leaves the remainder, examining everything is what makes the next read wait for more,
/// and a message larger than the buffer still arrives whole.
///
/// Every test here drives it through the <see cref="PipeReader"/> API rather than its own, so
/// they would equally pass against the reader being replaced. That is deliberate: it is the
/// contract being pinned, not the implementation.
/// </remarks>
public class StreamBufferReaderTests
{
    /// <summary>A stream that hands over its content in fixed-size pieces, as a socket would.</summary>
    private sealed class ChunkedStream(byte[] content, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(chunk, count), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int take = Math.Min(Math.Min(chunk, buffer.Length), content.Length - _position);
            content.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static PipeReader ReaderOver(byte[] content, int chunk = 7, int bufferSize = 64)
        => new StreamBufferReader(new ChunkedStream(content, chunk), bufferSize, minimumReadSpace: 16);

    /// <summary>Reads everything the reader will give, consuming as it goes.</summary>
    private static async Task<byte[]> DrainAsync(PipeReader reader)
    {
        var sink = new List<byte>();
        while (true)
        {
            var result = await reader.ReadAsync();
            foreach (var segment in result.Buffer)
                sink.AddRange(segment.ToArray());

            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
                return [.. sink];
        }
    }

    [Theory]
    [InlineData(1, 64)]
    [InlineData(7, 64)]
    [InlineData(4096, 64)]
    [InlineData(3, 16)]
    public async Task EveryOctetArrivesInOrderWhateverTheChunking(int chunk, int bufferSize)
    {
        var content = new byte[10_000];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)(i % 251);

        var reader = new StreamBufferReader(new ChunkedStream(content, chunk), bufferSize, minimumReadSpace: 16);

        Assert.Equal(content, await DrainAsync(reader));
    }

    [Fact]
    public async Task ConsumingAPrefixLeavesTheRemainder()
    {
        var content = Encoding.ASCII.GetBytes("ABCDEFGHIJ");
        var reader = ReaderOver(content, chunk: 10);

        var first = await reader.ReadAsync();
        Assert.True(first.Buffer.Length >= 10);

        // Consume four octets and examine only those four, so the next read may return at once.
        reader.AdvanceTo(first.Buffer.GetPosition(4), first.Buffer.GetPosition(4));

        var second = await reader.ReadAsync();
        Assert.Equal("EFGHIJ", Encoding.ASCII.GetString(second.Buffer.ToArray()));
    }

    [Fact]
    public async Task ExaminingEverythingMakesTheNextReadFetchMore()
    {
        var content = Encoding.ASCII.GetBytes("0123456789");
        var reader = ReaderOver(content, chunk: 4);

        var first = await reader.ReadAsync();
        Assert.Equal("0123", Encoding.ASCII.GetString(first.Buffer.ToArray()));

        // Consumed nothing, examined all: the parser could not complete a message, so the next
        // read has to wait for octets rather than hand the same four back forever.
        reader.AdvanceTo(first.Buffer.Start, first.Buffer.End);

        var second = await reader.ReadAsync();
        Assert.Equal("01234567", Encoding.ASCII.GetString(second.Buffer.ToArray()));
    }

    [Fact]
    public async Task NotExaminingEverythingReturnsImmediatelyWithTheSameOctets()
    {
        var content = Encoding.ASCII.GetBytes("0123456789");
        var reader = ReaderOver(content, chunk: 4);

        var first = await reader.ReadAsync();
        reader.AdvanceTo(first.Buffer.Start, first.Buffer.GetPosition(2));

        // Two octets are still unexamined, so there is no reason to go to the stream.
        var second = await reader.ReadAsync();
        Assert.Equal("0123", Encoding.ASCII.GetString(second.Buffer.ToArray()));
    }

    [Fact]
    public async Task AMessageLargerThanTheBufferStillArrivesWhole()
    {
        // Nothing is consumed until the whole thing is present, so the buffer has to grow past
        // its initial size rather than stall.
        var content = new byte[5_000];
        Random.Shared.NextBytes(content);

        var reader = new StreamBufferReader(new ChunkedStream(content, 64), bufferSize: 64, minimumReadSpace: 16);

        ReadResult result;
        while (true)
        {
            result = await reader.ReadAsync();
            if (result.Buffer.Length >= content.Length || result.IsCompleted) break;
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }

        Assert.Equal(content, result.Buffer.ToArray());
    }

    [Fact]
    public async Task TheEndOfTheStreamIsReportedAndTheTailIsNotLost()
    {
        var content = Encoding.ASCII.GetBytes("tail");
        var reader = ReaderOver(content, chunk: 2);

        ReadResult result;
        do
        {
            result = await reader.ReadAsync();
            if (!result.IsCompleted)
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
        while (!result.IsCompleted);

        Assert.Equal("tail", Encoding.ASCII.GetString(result.Buffer.ToArray()));
    }

    [Fact]
    public async Task AnEmptyStreamCompletesWithoutOctets()
    {
        var reader = ReaderOver([], chunk: 4);

        var result = await reader.ReadAsync();

        Assert.True(result.IsCompleted);
        Assert.Equal(0, result.Buffer.Length);
    }

    [Fact]
    public async Task TryReadOffersOnlyWhatIsAlreadyHeld()
    {
        var content = Encoding.ASCII.GetBytes("0123456789");
        var reader = ReaderOver(content, chunk: 4);

        // Nothing has been read from the stream yet, so there is nothing to offer.
        Assert.False(reader.TryRead(out _));

        var first = await reader.ReadAsync();
        reader.AdvanceTo(first.Buffer.Start, first.Buffer.GetPosition(1));

        Assert.True(reader.TryRead(out var queued));
        Assert.Equal("0123", Encoding.ASCII.GetString(queued.Buffer.ToArray()));
    }

    [Fact]
    public async Task ACancelledReadIsReportedOnceAndThenClears()
    {
        var reader = ReaderOver(Encoding.ASCII.GetBytes("abcd"), chunk: 2);

        reader.CancelPendingRead();
        var cancelled = await reader.ReadAsync();
        Assert.True(cancelled.IsCanceled);

        reader.AdvanceTo(cancelled.Buffer.Start, cancelled.Buffer.Start);

        var next = await reader.ReadAsync();
        Assert.False(next.IsCanceled);
    }

    [Fact]
    public async Task PipelinedMessagesAreEachConsumedInTurn()
    {
        // Three messages in one read. The shape that matters: consuming one leaves the next two
        // in place, which is how a pipelined connection makes progress without re-reading.
        var content = Encoding.ASCII.GetBytes("one|two|three|");
        var reader = ReaderOver(content, chunk: 64);

        var seen = new List<string>();
        var result = await reader.ReadAsync();
        var buffer = result.Buffer;

        for (int i = 0; i < 3; i++)
        {
            var delimiter = buffer.PositionOf((byte)'|');
            Assert.NotNull(delimiter);

            seen.Add(Encoding.ASCII.GetString(buffer.Slice(0, delimiter.Value).ToArray()));
            buffer = buffer.Slice(buffer.GetPosition(1, delimiter.Value));
        }

        reader.AdvanceTo(buffer.Start);

        Assert.Equal(["one", "two", "three"], seen);
    }

    [Fact]
    public void CompletingTwiceIsHarmless()
    {
        var reader = ReaderOver(Encoding.ASCII.GetBytes("abc"));

        reader.Complete();
        reader.Complete();

        Assert.Throws<ObjectDisposedException>(() => reader.TryRead(out _));
    }
}
