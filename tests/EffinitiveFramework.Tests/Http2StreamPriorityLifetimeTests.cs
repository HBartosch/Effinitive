using EffinitiveFramework.Core.Http2;
using EffinitiveFramework.Core.Http2.Hpack;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A stream's entry in the dependency tree has to go when the stream does.
/// </summary>
/// <remarks>
/// RFC 9113 §5.3.4 says a closed stream leaves the dependency tree and its children are
/// reparented onto its parent, which is what <c>RemoveStream</c> implements. Nothing was
/// calling it: every stream was registered on HEADERS and none was ever taken out, so the
/// tree grew for the whole life of the connection.
///
/// A stream lasts one request while an HTTP/2 connection is meant to stay open, so a tree
/// that is only ever added to grows with how long the peer stays connected rather than with
/// how much is in flight. Every request leaves a node, its <c>Http2StreamPriority</c> and its
/// children list behind, so a connection that answers enough requests holds all of them.
/// </remarks>
public class Http2StreamPriorityLifetimeTests
{
    /// <summary>Reads from a fixed script of frames and swallows whatever is written back.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly MemoryStream _inbound;

        public ScriptedStream(byte[] inbound) => _inbound = new MemoryStream(inbound);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inbound.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

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

    /// <summary>The preface, an empty SETTINGS, then <paramref name="count"/> request-and-reset pairs.</summary>
    private static byte[] RequestsThenResets(int count)
    {
        var script = new List<byte>();
        script.AddRange(Http2Constants.ClientPreface.ToArray());
        WriteFrameHeader(script, 0, Http2Constants.FrameTypeSettings, 0, 0);

        var encoder = new HpackEncoder();

        for (int i = 0; i < count; i++)
        {
            int streamId = 1 + (i * 2);

            var block = encoder.EncodeHeaders(
            [
                (":method", "GET"),
                (":scheme", "http"),
                (":authority", "localhost"),
                (":path", "/")
            ]);

            WriteFrameHeader(script, block.Length, Http2Constants.FrameTypeHeaders,
                (byte)(Http2Constants.FlagEndHeaders | Http2Constants.FlagEndStream), streamId);
            script.AddRange(block);

            // RST_STREAM with CANCEL, which is how a client abandons a request it no longer
            // wants. The stream is finished either way; the question is whether its node went
            // with it.
            WriteFrameHeader(script, 4, Http2Constants.FrameTypeRstStream, 0, streamId);
            script.AddRange([0, 0, 0, 8]);
        }

        return [.. script];
    }

    [Fact]
    public async Task TheDependencyTreeDoesNotGrowWithTheNumberOfRequestsServed()
    {
        const int requests = 200;

        var transport = new ScriptedStream(RequestsThenResets(requests));
        var connection = new Http2Connection(transport);

        await connection.ProcessAsync(CancellationToken.None);

        // Every stream in the script was reset, so nothing should be left behind. The assertion
        // is on the count rather than on emptiness so the failure message carries the scale.
        Assert.Equal(0, connection.TrackedPriorityStreamCount);

        await connection.DisposeAsync();
    }

    [Fact]
    public void RemovingAStreamReparentsItsChildrenRatherThanOrphaningThem()
    {
        var scheduler = new StreamPriorityScheduler();

        scheduler.RegisterStream(1);
        scheduler.RegisterStream(3, new Http2StreamPriority { DependsOn = 1, Weight = 16 });
        scheduler.RegisterStream(5, new Http2StreamPriority { DependsOn = 3, Weight = 16 });

        Assert.Equal(3, scheduler.TrackedStreamCount);

        scheduler.RemoveStream(3);

        // 5 outlives its parent, so it has to end up somewhere rather than be dropped
        // (RFC 9113 §5.3.4).
        Assert.Equal(2, scheduler.TrackedStreamCount);
    }
}
