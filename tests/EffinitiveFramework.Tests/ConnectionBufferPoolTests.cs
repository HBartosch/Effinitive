using EffinitiveFramework.Core.Http2;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// The per-connection write buffers: a buffer handed back must be reusable, and a connection
/// must not grow its retained memory just because it stayed open.
/// </summary>
/// <remarks>
/// This pool exists because <c>ArrayPool&lt;byte&gt;.Shared</c> assumes rent and return happen
/// on one thread, and the HTTP/2 write path rents on a stream's handler and returns on the
/// connection's single writer. The properties worth pinning are therefore the ones a careless
/// reimplementation would break: an array is never handed to two renters at once, a rented
/// array is always at least as long as asked for, retention is bounded, and anything too large
/// to belong here is passed through rather than mis-filed.
/// </remarks>
public class ConnectionBufferPoolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1024)]
    [InlineData(16384)]
    [InlineData(40000)]
    public void ARentedArrayIsNeverShorterThanAsked(int requested)
    {
        var pool = new ConnectionBufferPool();

        Assert.True(pool.Rent(requested).Length >= requested);
    }

    [Fact]
    public void AReturnedArrayComesBackRatherThanBeingReallocated()
    {
        var pool = new ConnectionBufferPool();

        var first = pool.Rent(100);
        pool.Return(first);

        Assert.Same(first, pool.Rent(100));
    }

    [Fact]
    public void ASmallerRequestCanReuseItsOwnSizeClass()
    {
        var pool = new ConnectionBufferPool();

        // 100 and 128 both land in the 128-byte class, so the array is interchangeable.
        var rented = pool.Rent(100);
        Assert.Equal(128, rented.Length);
        pool.Return(rented);

        Assert.Same(rented, pool.Rent(128));
    }

    [Fact]
    public void TwoRentersNeverReceiveTheSameArray()
    {
        var pool = new ConnectionBufferPool();

        var seeded = pool.Rent(256);
        pool.Return(seeded);

        Assert.Same(seeded, pool.Rent(256));
        Assert.NotSame(seeded, pool.Rent(256));
    }

    [Fact]
    public void ConcurrentRentersNeverReceiveTheSameArray()
    {
        var pool = new ConnectionBufferPool();

        // Fill one size class, then have many threads empty it at once. Handing the same
        // array to two callers is the failure this guards: both would then write framing
        // bytes into it and one response would arrive corrupted.
        var seeds = new List<byte[]>();
        for (int i = 0; i < 8; i++)
            seeds.Add(pool.Rent(256));
        foreach (var seed in seeds)
            pool.Return(seed);

        var taken = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        Parallel.For(0, 64, _ => taken.Add(pool.Rent(256)));

        var distinct = taken.Distinct(ReferenceEqualityComparer.Instance).Count();
        Assert.Equal(taken.Count, distinct);
    }

    [Fact]
    public void RetentionPerSizeClassIsBounded()
    {
        var pool = new ConnectionBufferPool();

        // Return far more than any class can hold. The surplus must be dropped for the
        // collector rather than accumulated, which is what keeps a long-lived connection from
        // behaving like a leak.
        var returned = new List<byte[]>();
        for (int i = 0; i < 200; i++)
        {
            var array = pool.Rent(256);
            returned.Add(array);
        }
        foreach (var array in returned)
            pool.Return(array);

        int reused = 0;
        var identities = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var array in returned)
            identities.Add(array);

        for (int i = 0; i < 200; i++)
        {
            if (identities.Contains(pool.Rent(256)))
                reused++;
        }

        Assert.True(reused <= 8, $"the 256-byte class handed back {reused} arrays; it should cap at 8");
    }

    [Fact]
    public void ABufferTooLargeForTheConnectionIsPassedThroughAndSurvivesARoundTrip()
    {
        var pool = new ConnectionBufferPool();

        // Above a maximum-size frame the shared pool is the right home, so the only thing that
        // matters is that returning it does not throw and renting still works afterwards.
        var large = pool.Rent(200_000);
        Assert.True(large.Length >= 200_000);

        pool.Return(large);

        Assert.True(pool.Rent(200_000).Length >= 200_000);
    }

    // Two pools, because "the same array came back" is true whether it came back from this
    // connection or from ArrayPool.Shared, and the whole point is which. A buffer that reaches
    // the shared pool is one the next connection can be handed, so a second pool receiving it
    // is the signal that it leaked out of the first.
    private static void AssertStaysOnItsOwnConnection(int length)
    {
        var owner = new ConnectionBufferPool();
        var other = new ConnectionBufferPool();

        var rented = owner.Rent(length);
        Assert.True(rented.Length >= length);
        owner.Return(rented);

        Assert.NotSame(rented, other.Rent(length));
        Assert.Same(rented, owner.Rent(length));
    }

    [Fact]
    public void AFullDataFrameStaysOnTheConnection()
    {
        // The write path asks for a frame *and its header*, so a full DATA frame is
        // FrameHeaderLength + SETTINGS_MAX_FRAME_SIZE: nine bytes above the frame size itself.
        // A ceiling set at the frame size missed every one of these by those nine bytes and
        // sent them to the shared pool, which is the contention this type exists to avoid. It
        // showed only on large bodies, because small responses never fill a frame.
        AssertStaysOnItsOwnConnection(
            Http2Constants.FrameHeaderLength + (int)Http2Constants.DefaultMaxFrameSize);
    }

    [Fact]
    public void TheCombinedHeadersAndDataBufferStaysOnTheConnection()
    {
        // The single-frame fast path emits HEADERS and DATA as one buffer, so it is larger
        // again by the encoded headers and a second frame header.
        AssertStaysOnItsOwnConnection(
            (Http2Constants.FrameHeaderLength * 2) + (int)Http2Constants.DefaultMaxFrameSize + 256);
    }

    [Fact]
    public void ReturningNothingIsHarmless()
    {
        var pool = new ConnectionBufferPool();

        pool.Return(null);
        pool.Return([]);

        Assert.True(pool.Rent(64).Length >= 64);
    }

    [Fact]
    public void AZeroLengthRequestDoesNotAllocate()
    {
        Assert.Empty(new ConnectionBufferPool().Rent(0));
    }

    [Fact]
    public void AnArrayOfAnOddLengthIsNotFiledIntoASizeClass()
    {
        var pool = new ConnectionBufferPool();

        // A 100-byte array could only have come from somewhere else. Filing it under the
        // 128-byte class would later hand a caller asking for 128 bytes an array of 100.
        pool.Return(new byte[100]);

        Assert.True(pool.Rent(128).Length >= 128);
    }
}
