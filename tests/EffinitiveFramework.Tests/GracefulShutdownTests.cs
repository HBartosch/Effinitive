using EffinitiveFramework.Core.Http2;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A server shutting down gracefully has to say so on the wire.
/// </summary>
/// <remarks>
/// RFC 9113 §6.8 says a server attempting to shut down gracefully SHOULD send GOAWAY, and RFC
/// 9114 §5.2 makes it the means by which an HTTP/3 shutdown is initiated. Without it a peer
/// cannot tell which streams were accepted and which it should retry elsewhere.
///
/// The trap is that a graceful shutdown reaches the connection *as* a cancellation, so the
/// obvious thing, passing the caller's token on to the send, means the frame is never written:
/// the token has already fired by the time the farewell is attempted, and the failure is
/// swallowed by the best-effort catch around it. Nothing looks wrong from inside the server.
/// </remarks>
public class GracefulShutdownTests
{
    /// <summary>A stream that reads from one buffer and records what was written to another.</summary>
    private sealed class LoopbackStream : Stream
    {
        private readonly MemoryStream _inbound;
        private readonly MemoryStream _outbound = new();

        public LoopbackStream(byte[] inbound) => _inbound = new MemoryStream(inbound);

        public byte[] Written => _outbound.ToArray();

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

    private static byte[] ClientPrefaceAndSettings()
    {
        var preface = Http2Constants.ClientPreface.ToArray();

        // An empty SETTINGS frame: 24-bit length 0, type 0x04, no flags, stream 0.
        var settings = new byte[] { 0, 0, 0, Http2Constants.FrameTypeSettings, 0, 0, 0, 0, 0 };

        return [.. preface, .. settings];
    }

    /// <summary>Finds a frame of the given type in what the connection wrote (RFC 9113 §4.1).</summary>
    private static bool ContainsFrame(byte[] written, byte frameType)
    {
        int offset = 0;
        while (offset + Http2Constants.FrameHeaderLength <= written.Length)
        {
            int length = (written[offset] << 16) | (written[offset + 1] << 8) | written[offset + 2];
            if (written[offset + 3] == frameType)
                return true;

            offset += Http2Constants.FrameHeaderLength + length;
        }

        return false;
    }

    [Fact]
    public async Task ShuttingDownSendsGoAwayEvenThoughShutdownArrivesAsACancellation()
    {
        var transport = new LoopbackStream(ClientPrefaceAndSettings());
        var connection = new Http2Connection(transport);

        // Exactly how a server shutdown reaches a connection: the token it was handed fires.
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        await connection.ProcessAsync(shutdown.Token);
        await connection.DisposeAsync();

        Assert.True(ContainsFrame(transport.Written, Http2Constants.FrameTypeGoAway),
            "no GOAWAY was written, so the peer was never told the connection was closing");
    }

    // The writer is also what flushes responses already queued. Linking its token to the
    // shutdown token killed it at the moment of shutdown, so anything still in the channel was
    // dropped and the client saw a truncated body rather than a short one.
    [Fact]
    public async Task FramesQueuedBeforeShutdownAreStillWritten()
    {
        var transport = new LoopbackStream(ClientPrefaceAndSettings());
        var connection = new Http2Connection(transport);

        await connection.ProcessAsync(CancellationToken.None);
        await connection.DisposeAsync();

        // The server's own SETTINGS is queued through the same writer, so its arrival shows the
        // writer ran to completion rather than being cut off.
        Assert.True(ContainsFrame(transport.Written, Http2Constants.FrameTypeSettings),
            "the server's SETTINGS never reached the wire, so the writer was cut off");
    }
}
