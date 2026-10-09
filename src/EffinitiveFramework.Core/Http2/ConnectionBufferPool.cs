using System.Buffers;
using System.Numerics;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// The buffers one connection uses to frame its outgoing bytes, kept on the connection.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArrayPool{T}.Shared"/> is built for renting and returning on the same thread: it
/// keeps one array per size class in thread-local storage and falls back to per-core stacks,
/// each behind a lock. The HTTP/2 write path cannot satisfy that. A response buffer is rented
/// on whichever thread pool thread happens to run the stream's handler, and it is returned on
/// the connection's single writer, because only the writer knows when the bytes have reached
/// the socket. The writer fills its one thread-local slot with the first array it hands back
/// and then takes a per-core lock for every array after it, for as long as the connection
/// lasts. Multiply that by the number of live connections and the locks are the bottleneck:
/// a capture at 1024 connections found 142 of 237 blocked threads inside
/// <c>SharedArrayPool.Return</c> called from the writer.
/// </para>
/// <para>
/// Keeping the buffers on the connection does not remove the hand-off between threads, but it
/// confines it. Contention is now between one connection's own streams rather than between
/// every connection in the process, and the slots are claimed with an interlocked exchange, so
/// a thread that loses a race takes the next slot instead of parking.
/// </para>
/// <para>
/// Retention is deliberately bounded. Anything larger than a maximum-size frame goes to the
/// shared pool, where a rare large buffer belongs, and each size class holds only a few arrays,
/// so an idle connection cannot sit on memory. A return that finds every slot taken simply
/// drops the array for the collector, which is the correct outcome: the pool exists to avoid
/// allocation, not to guarantee it.
/// </para>
/// </remarks>
internal sealed class ConnectionBufferPool
{
    // 64 bytes is below the smallest frame worth pooling. The ceiling is 32 KiB rather than
    // the 16 KiB of SETTINGS_MAX_FRAME_SIZE's default (RFC 9113 §6.5.2), because the buffers
    // the write path actually asks for are a frame *and its header*: a full DATA frame needs
    // FrameHeaderLength + 16384 = 16393 bytes, and the combined HEADERS-plus-DATA buffer is
    // larger still. A 16 KiB ceiling missed every one of them by a few bytes and sent them to
    // the shared pool, which is the exact contention this type exists to avoid — and it did so
    // only for large bodies, so small-response traffic looked fixed while static files did not.
    private const int MinShift = 6;
    private const int MaxShift = 15;
    private const int MinSize = 1 << MinShift;
    private const int MaxSize = 1 << MaxShift;

    // Small buffers are the common case — a HEADERS frame is tens of bytes — so they get more
    // slots. Large ones get few, because holding 16 KiB per slot across many connections adds
    // up faster than it saves.
    private const int SmallDepth = 8;
    private const int LargeDepth = 2;
    private const int LargeThreshold = 1024;

    private readonly byte[]?[][] _buckets;

    internal ConnectionBufferPool()
    {
        _buckets = new byte[]?[MaxShift - MinShift + 1][];
        for (int i = 0; i < _buckets.Length; i++)
            _buckets[i] = new byte[]?[SizeOf(i) <= LargeThreshold ? SmallDepth : LargeDepth];
    }

    private static int SizeOf(int index) => 1 << (index + MinShift);

    /// <summary>The size class that can hold <paramref name="length"/>, rounded up.</summary>
    private static int IndexFor(int length)
    {
        int shift = BitOperations.Log2((uint)(length - 1)) + 1;
        return Math.Max(shift, MinShift) - MinShift;
    }

    /// <summary>
    /// An array of at least <paramref name="minimumLength"/> bytes. Its contents are undefined.
    /// </summary>
    internal byte[] Rent(int minimumLength)
    {
        if (minimumLength <= 0)
            return [];

        // A buffer this large is rare enough that the shared pool's locking does not matter,
        // and holding one per connection would cost more than it saves.
        if (minimumLength > MaxSize)
            return ArrayPool<byte>.Shared.Rent(minimumLength);

        var slots = _buckets[IndexFor(minimumLength)];
        for (int i = 0; i < slots.Length; i++)
        {
            // Exchange rather than read-then-clear: two renters must not walk away with the
            // same array, and the loser of the race simply moves to the next slot.
            var candidate = Interlocked.Exchange(ref slots[i], null);
            if (candidate is not null)
                return candidate;
        }

        return GC.AllocateUninitializedArray<byte>(SizeOf(IndexFor(minimumLength)));
    }

    /// <summary>
    /// Offer an array back. The caller must not touch it afterwards.
    /// </summary>
    internal void Return(byte[]? array)
    {
        if (array is null || array.Length == 0)
            return;

        int length = array.Length;

        // Only an exact size class is accepted. Filing a 100-byte array under the 128-byte
        // class would later hand a caller asking for 128 an array of 100, and the framing
        // write past the end would throw inside the writer.
        if (!BitOperations.IsPow2((uint)length))
            return;

        // Above our largest class the array came from the shared pool, so it goes home. The
        // power-of-two check above also guards this: Return rejects a length that is not one
        // of its bucket sizes, and that exception would surface on the writer and tear the
        // connection down. Anything else is simply dropped, which is always safe.
        if (length > MaxSize)
        {
            ArrayPool<byte>.Shared.Return(array);
            return;
        }

        if (length < MinSize)
            return;

        var slots = _buckets[IndexFor(length)];
        for (int i = 0; i < slots.Length; i++)
        {
            if (Interlocked.CompareExchange(ref slots[i], array, null) is null)
                return;
        }

        // Every slot is taken. Dropping the array is the bounded outcome; the alternative is a
        // connection that grows its retained memory for as long as it stays open.
    }
}
