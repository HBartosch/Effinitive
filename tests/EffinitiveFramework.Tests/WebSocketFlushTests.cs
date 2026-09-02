using System.Buffers.Binary;
using EffinitiveFramework.Core.WebSocket;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// WebSocketConnection defers its flush when another frame is already buffered,
/// so a run of replies costs one write instead of one each. What it must not do
/// is defer on a frame that is only partly here.
///
/// RFC 6455 §5.2 gives every frame an explicit length, so a frame becomes
/// actionable only once that many payload bytes have arrived. Bytes that do not
/// yet complete a frame offer nothing to process, and holding a finished reply
/// on them stalls a client that is waiting for exactly that reply before it
/// sends anything further.
/// </summary>
public class WebSocketFlushTests
{
    [Fact]
    public async Task ACompleteFrame_IsAnsweredEvenWhenAPartialFrameTrailsIt()
    {
        // One byte of the next frame is enough to make "are there bytes left?"
        // true while "is there a frame left?" is false.
        var stream = new OneShotDuplexStream([.. ClientFrame("hello"), .. ClientFrame("next")[..1]]);
        var connection = new WebSocketConnection(stream);

        var message = await connection.ReceiveAsync(TestTimeout());
        Assert.NotNull(message);
        Assert.Equal("hello", message!.Value.GetText());

        await connection.SendAsync(message.Value.Data, WebSocketMessageType.Text, TestTimeout());

        Assert.True(stream.Written.Length > 0,
            "the echo was still buffered: a partly-arrived next frame held back the answer to a frame that had fully arrived");
    }

    [Fact]
    public async Task TwoCompleteFrames_AreBatchedIntoOneFlush()
    {
        var stream = new OneShotDuplexStream([.. ClientFrame("one"), .. ClientFrame("two")]);
        var connection = new WebSocketConnection(stream);

        var first = await connection.ReceiveAsync(TestTimeout());
        Assert.NotNull(first);
        await connection.SendAsync(first!.Value.Data, WebSocketMessageType.Text, TestTimeout());

        // The whole point of the optimisation: with another frame ready to
        // process, the first response waits so both go out together.
        Assert.Equal(0, stream.Written.Length);

        var second = await connection.ReceiveAsync(TestTimeout());
        Assert.NotNull(second);
        await connection.SendAsync(second!.Value.Data, WebSocketMessageType.Text, TestTimeout());

        Assert.True(stream.Written.Length > 0, "the batch was never flushed");
    }

    [Fact]
    public async Task ASingleFrame_IsFlushedImmediately()
    {
        var stream = new OneShotDuplexStream(ClientFrame("solo"));
        var connection = new WebSocketConnection(stream);

        var message = await connection.ReceiveAsync(TestTimeout());
        Assert.NotNull(message);
        await connection.SendAsync(message!.Value.Data, WebSocketMessageType.Text, TestTimeout());

        Assert.True(stream.Written.Length > 0);
    }

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    /// <summary>A masked client text frame. RFC 6455 §5.3 requires client masking.</summary>
    private static byte[] ClientFrame(string text)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(text);
        var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var header = new List<byte> { 0x81 }; // FIN + text

        if (payload.Length < 126)
        {
            header.Add((byte)(0x80 | payload.Length));
        }
        else
        {
            header.Add(0x80 | 126);
            var len = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)payload.Length);
            header.AddRange(len);
        }
        header.AddRange(mask);

        var masked = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++)
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);

        return [.. header, .. masked];
    }

    /// <summary>
    /// Hands over one buffer of client bytes and then stops, without reporting
    /// end of stream. That is what a client mid-conversation looks like: no more
    /// data yet, and the connection still open.
    /// </summary>
    private sealed class OneShotDuplexStream(byte[] inbound) : Stream
    {
        private readonly MemoryStream _written = new();
        private int _offset;

        public MemoryStream Written => _written;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < inbound.Length)
            {
                var n = Math.Min(buffer.Length, inbound.Length - _offset);
                inbound.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }

            // Never completes on its own; the test's own timeout is the bound.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
