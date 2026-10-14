using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;
using EffinitiveFramework.Core.Http2.Hpack;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A response whose body is still a stream is framed out of that stream, not out of a copy of
/// it.
/// </summary>
/// <remarks>
/// A static file arrives as a stream rather than as bytes, and the framing has to hold whatever
/// the path does with it: the bytes reach the wire in order and in full, no DATA frame exceeds
/// the peer's maximum frame size, the last frame carries END_STREAM and no frame before it
/// does, an empty body ends on the HEADERS frame, and the source is disposed however the
/// response ends. A source shorter than its declared length still has to finish the stream
/// rather than wait for bytes that are not coming.
///
/// These hold however the body reaches the wire, whether it is buffered first or framed straight
/// out of the stream.
/// </remarks>
public class Http2StreamedBodyTests
{
    /// <summary>Reads a scripted client conversation and records everything written back.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _inbound;
        private readonly MemoryStream _outbound = new();

        public RecordingStream(byte[] inbound) => _inbound = new MemoryStream(inbound);

        public byte[] Written { get { lock (_outbound) return _outbound.ToArray(); } }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inbound.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_outbound) _outbound.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_outbound) _outbound.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A body source that records whether the framing path disposed it.</summary>
    private sealed class TrackedStream(byte[] content) : MemoryStream(content)
    {
        public bool Disposed { get; private set; }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record Frame(byte Type, byte Flags, int StreamId, byte[] Payload);

    private static void WriteFrameHeader(List<byte> into, int length, byte type, byte flags, int streamId)
    {
        into.Add((byte)(length >> 16));
        into.Add((byte)(length >> 8));
        into.Add((byte)length);
        into.Add(type);
        into.Add(flags);
        into.Add((byte)(streamId >> 24));
        into.Add((byte)(streamId >> 16));
        into.Add((byte)(streamId >> 8));
        into.Add((byte)streamId);
    }

    /// <summary>The preface, an empty SETTINGS, and one GET on stream 1.</summary>
    private static byte[] OneRequest()
    {
        var script = new List<byte>();
        script.AddRange(Http2Constants.ClientPreface.ToArray());
        WriteFrameHeader(script, 0, Http2Constants.FrameTypeSettings, 0, 0);

        var block = new HpackEncoder().EncodeHeaders(
        [
            (":method", "GET"),
            (":scheme", "http"),
            (":authority", "localhost"),
            (":path", "/file")
        ]);
        WriteFrameHeader(script, block.Length, Http2Constants.FrameTypeHeaders,
            (byte)(Http2Constants.FlagEndHeaders | Http2Constants.FlagEndStream), 1);
        script.AddRange(block);

        return [.. script];
    }

    private static List<Frame> Parse(byte[] written)
    {
        var frames = new List<Frame>();
        int offset = 0;
        while (offset + Http2Constants.FrameHeaderLength <= written.Length)
        {
            int length = (written[offset] << 16) | (written[offset + 1] << 8) | written[offset + 2];
            byte type = written[offset + 3];
            byte flags = written[offset + 4];
            int streamId = (written[offset + 5] << 24) | (written[offset + 6] << 16)
                         | (written[offset + 7] << 8) | written[offset + 8];

            int payloadStart = offset + Http2Constants.FrameHeaderLength;
            if (payloadStart + length > written.Length) break;

            frames.Add(new Frame(type, flags, streamId, written[payloadStart..(payloadStart + length)]));
            offset = payloadStart + length;
        }
        return frames;
    }

    /// <summary>Runs one request whose response body is the given stream, and returns the frames.</summary>
    private static async Task<List<Frame>> ExchangeAsync(Stream body, long declaredLength)
    {
        var transport = new RecordingStream(OneRequest());
        var response = new HttpResponse
        {
            StatusCode = 200,
            ContentType = "application/octet-stream",
            BodyStream = body,
            BodyStreamLength = declaredLength
        };

        var connection = new Http2Connection(transport, _ => Task.FromResult(response));

        await connection.ProcessAsync(CancellationToken.None);
        await connection.DisposeAsync();

        return Parse(transport.Written);
    }

    [Fact]
    public async Task EveryByteOfAStreamedBodyReachesTheWireInOrder()
    {
        // Larger than one maximum-size frame, so the body is necessarily split and the
        // reassembly actually proves something.
        var content = new byte[(int)(Http2Constants.DefaultMaxFrameSize * 2.5)];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)(i % 251);

        var frames = await ExchangeAsync(new TrackedStream(content), content.Length);

        var body = frames
            .Where(f => f.Type == Http2Constants.FrameTypeData && f.StreamId == 1)
            .SelectMany(f => f.Payload)
            .ToArray();

        Assert.Equal(content.Length, body.Length);
        Assert.Equal(content, body);
    }

    [Fact]
    public async Task NoDataFrameExceedsTheMaximumFrameSize()
    {
        var content = new byte[(int)(Http2Constants.DefaultMaxFrameSize * 2.5)];

        var frames = await ExchangeAsync(new TrackedStream(content), content.Length);

        // RFC 9113 §4.2: a frame larger than SETTINGS_MAX_FRAME_SIZE is a connection error, so
        // the split has to respect it no matter how much window the peer has granted.
        foreach (var frame in frames.Where(f => f.Type == Http2Constants.FrameTypeData))
            Assert.True(frame.Payload.Length <= Http2Constants.DefaultMaxFrameSize,
                $"a DATA frame of {frame.Payload.Length} bytes exceeds the maximum frame size");
    }

    [Fact]
    public async Task OnlyTheFinalFrameEndsTheStream()
    {
        var content = new byte[(int)(Http2Constants.DefaultMaxFrameSize * 2.5)];

        var frames = await ExchangeAsync(new TrackedStream(content), content.Length);

        var onStream = frames.Where(f => f.StreamId == 1).ToList();
        Assert.NotEmpty(onStream);

        // Ending the stream early truncates the body as far as the client is concerned, and
        // never ending it leaves the request hanging.
        for (int i = 0; i < onStream.Count - 1; i++)
            Assert.True((onStream[i].Flags & Http2Constants.FlagEndStream) == 0,
                $"frame {i} of {onStream.Count} ended the stream before the body was finished");

        Assert.True((onStream[^1].Flags & Http2Constants.FlagEndStream) != 0,
            "the last frame did not end the stream");
    }

    [Fact]
    public async Task AnEmptyBodyEndsOnTheHeadersFrame()
    {
        var frames = await ExchangeAsync(new TrackedStream([]), 0);

        var onStream = frames.Where(f => f.StreamId == 1).ToList();
        var headers = Assert.Single(onStream.Where(f => f.Type == Http2Constants.FrameTypeHeaders));

        // A DATA frame carrying nothing is pure overhead when the HEADERS frame can say it
        // (RFC 9113 §8.1).
        Assert.True((headers.Flags & Http2Constants.FlagEndStream) != 0,
            "an empty body should end the stream on HEADERS");
        Assert.DoesNotContain(onStream, f => f.Type == Http2Constants.FrameTypeData && f.Payload.Length > 0);
    }

    [Fact]
    public async Task TheSourceIsDisposedOnceTheResponseHasBeenSent()
    {
        var body = new TrackedStream(new byte[4096]);

        await ExchangeAsync(body, 4096);

        // The source is a file handle on the static path. Leaving it open leaks one per request.
        Assert.True(body.Disposed, "the body stream was not disposed");
    }

    [Fact]
    public async Task ASourceShorterThanItsDeclaredLengthStillEndsTheStream()
    {
        // The declared length is what the headers promised; the source has less. The response
        // has to finish rather than wait forever for bytes that are not coming.
        var body = new TrackedStream(new byte[1000]);

        var frames = await ExchangeAsync(body, declaredLength: 50_000);

        var onStream = frames.Where(f => f.StreamId == 1).ToList();
        Assert.True((onStream[^1].Flags & Http2Constants.FlagEndStream) != 0,
            "a truncated source left the stream open");
        Assert.True(body.Disposed, "the body stream was not disposed");
    }
}
