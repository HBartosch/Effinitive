using EffinitiveFramework.Core.Http2;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// The send windows are the one piece of HTTP/2 state that many threads touch at once, and
/// getting them wrong overruns the peer rather than merely slowing things down.
/// </summary>
/// <remarks>
/// RFC 9113 §6.9.1 is explicit: a sender must not send more than the window allows, and the
/// window must not go negative through the sender's own accounting. The connection window is
/// shared by every stream on the connection, so every response reserves from the same counter
/// concurrently.
///
/// These tests exist to guard a change of mechanism. The counter was guarded by a Monitor,
/// which is correct but serialises every response on a connection: under load a live capture
/// showed 186 of 356 threads parked on that one lock. Replacing it with a compare-and-swap
/// loop removes the parking, and the properties below are what must survive that swap —
/// nothing is ever over-granted, a failed reservation consumes nothing, and a reservation
/// spanning both windows is all-or-nothing.
/// </remarks>
public class Http2FlowControlTests
{
    private sealed class NullStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken c = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static Http2Connection NewConnection() => new(new NullStream());

    // RFC 9113 §6.9.2: the connection window starts at 65,535 and SETTINGS does not change it.
    private const int InitialConnectionWindow = 65535;

    [Fact]
    public void TheConnectionWindowStartsAtTheValueTheRfcMandates()
    {
        Assert.Equal(InitialConnectionWindow, NewConnection().ConnectionWindowSize);
    }

    [Fact]
    public void ConcurrentReserversNeverOverdrawTheConnectionWindow()
    {
        var connection = NewConnection();

        // Sized so the window cannot satisfy everyone: 64 threads each wanting 2,048 bytes is
        // 131,072 against a 65,535 window, so roughly half must be refused. A mechanism that
        // loses a race here over-grants, which on the wire is a flow-control violation.
        const int threads = 64, each = 2048;
        int granted = 0;

        Parallel.For(0, threads, _ =>
        {
            if (connection.TryReserveConnectionWindow(each))
                Interlocked.Add(ref granted, each);
        });

        Assert.True(granted <= InitialConnectionWindow,
            $"granted {granted} bytes from a {InitialConnectionWindow} byte window");
        Assert.Equal(InitialConnectionWindow - granted, connection.ConnectionWindowSize);
        Assert.True(connection.ConnectionWindowSize >= 0, "the window went negative");
    }

    [Fact]
    public void ARefusedReservationConsumesNothing()
    {
        var connection = NewConnection();

        Assert.True(connection.TryReserveConnectionWindow(InitialConnectionWindow - 10));
        Assert.Equal(10, connection.ConnectionWindowSize);

        // Asking for more than is left must leave the remainder intact rather than taking
        // what it can: the caller sends all-or-nothing, so a partial debit would be lost.
        Assert.False(connection.TryReserveConnectionWindow(11));
        Assert.Equal(10, connection.ConnectionWindowSize);
    }

    [Fact]
    public void ReleasingReturnsExactlyWhatWasReserved()
    {
        var connection = NewConnection();

        Assert.True(connection.TryReserveConnectionWindow(5000));
        connection.ReleaseConnectionWindow(5000);

        Assert.Equal(InitialConnectionWindow, connection.ConnectionWindowSize);
    }

    [Fact]
    public void ReleasingNothingIsANoOp()
    {
        var connection = NewConnection();

        connection.ReleaseConnectionWindow(0);
        connection.ReleaseConnectionWindow(-1);

        Assert.Equal(InitialConnectionWindow, connection.ConnectionWindowSize);
    }

    [Fact]
    public async Task AWaiterIsWokenWhenWindowIsReturned()
    {
        var connection = NewConnection();

        Assert.True(connection.TryReserveConnectionWindow(InitialConnectionWindow));
        Assert.Equal(0, connection.ConnectionWindowSize);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = connection.AcquireConnectionWindowAsync(100, cts.Token);

        // Nothing can be granted from an exhausted window, so the acquire must still be
        // outstanding. Without this the next assertion would pass even if it never waited.
        Assert.False(pending.IsCompleted);

        connection.ReleaseConnectionWindow(100);

        Assert.Equal(100, await pending);
    }

    [Fact]
    public async Task AnAcquireGrantsNoMoreThanIsAvailable()
    {
        var connection = NewConnection();

        Assert.True(connection.TryReserveConnectionWindow(InitialConnectionWindow - 40));

        // 40 bytes left, 1,000 wanted: a partial grant is correct here, because the sender
        // splits its DATA across frames (RFC 9113 §6.9.1).
        Assert.Equal(40, await connection.AcquireConnectionWindowAsync(1000, CancellationToken.None));
        Assert.Equal(0, connection.ConnectionWindowSize);
    }

    // ── The pair of windows ──────────────────────────────────────────────────────────────

    [Fact]
    public void ReservingBothWindowsIsAllOrNothing()
    {
        var connection = NewConnection();
        var stream = new Http2Stream(1, initialWindowSize: 1000);

        // The stream can cover it and the connection cannot, so the whole reservation must
        // fail and the stream's window must come back. Leaking it here would strand window on
        // a stream that never sends, and the peer would wait forever for data.
        Assert.True(connection.TryReserveConnectionWindow(InitialConnectionWindow - 100));
        Assert.False(connection.TryReserveBothWindows(stream, 500));

        Assert.Equal(1000, stream.WindowSize);
        Assert.Equal(100, connection.ConnectionWindowSize);
    }

    [Fact]
    public void ReservingBothWindowsFailsWithoutTouchingTheConnectionWhenTheStreamIsShort()
    {
        var connection = NewConnection();
        var stream = new Http2Stream(1, initialWindowSize: 10);

        Assert.False(connection.TryReserveBothWindows(stream, 500));

        Assert.Equal(10, stream.WindowSize);
        Assert.Equal(InitialConnectionWindow, connection.ConnectionWindowSize);
    }

    [Fact]
    public void ReservingBothWindowsDebitsBothOnSuccess()
    {
        var connection = NewConnection();
        var stream = new Http2Stream(1, initialWindowSize: 1000);

        Assert.True(connection.TryReserveBothWindows(stream, 400));

        Assert.Equal(600, stream.WindowSize);
        Assert.Equal(InitialConnectionWindow - 400, connection.ConnectionWindowSize);
    }

    // ── The per-stream window, which has the same shape ──────────────────────────────────

    [Fact]
    public void ConcurrentReserversNeverOverdrawAStreamWindow()
    {
        var stream = new Http2Stream(1, initialWindowSize: 65535);

        const int threads = 64, each = 2048;
        int granted = 0;

        Parallel.For(0, threads, _ =>
        {
            if (stream.TryReserveWindow(each))
                Interlocked.Add(ref granted, each);
        });

        Assert.True(granted <= 65535, $"granted {granted} from a 65535 byte stream window");
        Assert.Equal(65535 - granted, stream.WindowSize);
        Assert.True(stream.WindowSize >= 0, "the stream window went negative");
    }

    [Fact]
    public async Task AStreamResetUnblocksASenderWaitingForWindow()
    {
        var stream = new Http2Stream(1, initialWindowSize: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = stream.AcquireWindowAsync(100, cts.Token);

        Assert.False(pending.IsCompleted);

        // RST_STREAM has to wake the sender rather than leave it parked on a window that will
        // never be granted, which is what Abort is for.
        stream.Abort();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public void AnAbortedStreamRefusesFurtherReservations()
    {
        var stream = new Http2Stream(1, initialWindowSize: 1000);
        stream.Abort();

        Assert.False(stream.TryReserveWindow(10));
        Assert.Equal(1000, stream.WindowSize);
    }

    [Fact]
    public void ASettingsReductionCanDriveAStreamWindowNegativeAndNoReservationSucceedsThen()
    {
        // RFC 9113 §6.9.2: lowering SETTINGS_INITIAL_WINDOW_SIZE shrinks every open stream's
        // window by the delta, and that is the one case where it may legitimately go negative.
        // Nothing may be sent until it climbs back above zero.
        var stream = new Http2Stream(1, initialWindowSize: 1000);
        Assert.True(stream.TryReserveWindow(900));

        stream.UpdateWindowSize(-500);

        Assert.True(stream.WindowSize < 0, "the reduction should have driven the window negative");
        Assert.False(stream.TryReserveWindow(1));

        stream.UpdateWindowSize(1000);
        Assert.True(stream.TryReserveWindow(1));
    }
}
