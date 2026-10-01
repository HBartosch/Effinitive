using EffinitiveFramework.Core.Http3;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// QPACK field section encoding (RFC 9204).
/// </summary>
/// <remarks>
/// The encoder writes into a buffer the caller owns, so it has to say when the buffer is too
/// small rather than overrun it, and it has to leave nothing half-written when it does. These
/// check the bytes it produces by decoding them back, which is the property that actually
/// matters, and check the three field line forms separately because each takes a different
/// branch: a full static table match, a name match with a literal value, and neither.
/// </remarks>
public class QpackEncoderTests
{
    private static List<(string name, string value)> RoundTrip(List<(string name, string value)> fields)
    {
        var buffer = new byte[4096];
        Assert.True(QpackEncoder.TryEncode(fields, buffer, out var written));

        return new QpackDecoder().Decode(buffer.AsSpan(0, written));
    }

    [Fact]
    public void FieldSectionPrefixIsTwoZeroBytes()
    {
        // RFC 9204 §4.5.1: Required Insert Count and Delta Base, both zero because only the
        // static table is referenced.
        var buffer = new byte[64];
        Assert.True(QpackEncoder.TryEncode([(":status", "200")], buffer, out var written));

        Assert.Equal(0x00, buffer[0]);
        Assert.Equal(0x00, buffer[1]);
        Assert.True(written > 2);
    }

    [Fact]
    public void AFullStaticTableMatchSurvivesARoundTrip()
    {
        // ":status: 200" is a single static entry, so it encodes as one indexed field line.
        var decoded = RoundTrip([(":status", "200")]);

        Assert.Equal(new[] { (":status", "200") }, decoded);
    }

    [Fact]
    public void ANameMatchWithALiteralValueSurvivesARoundTrip()
    {
        // The name is in the static table; this value is not.
        var decoded = RoundTrip([("content-type", "application/vnd.custom+json")]);

        Assert.Equal(new[] { ("content-type", "application/vnd.custom+json") }, decoded);
    }

    [Fact]
    public void AFieldInNeitherTableSurvivesARoundTrip()
    {
        var decoded = RoundTrip([("x-correlation-id", "7f3dacad-1fab-4c2e-9d10-0b1e2c3d4e5f")]);

        Assert.Equal(new[] { ("x-correlation-id", "7f3dacad-1fab-4c2e-9d10-0b1e2c3d4e5f") }, decoded);
    }

    [Fact]
    public void AWholeResponseFieldSectionSurvivesARoundTrip()
    {
        List<(string name, string value)> fields =
        [
            (":status", "200"),
            ("content-type", "application/json"),
            ("content-length", "2"),
            ("etag", "\"e3b0c44298fc1c14\""),
            ("last-modified", "Wed, 01 Oct 2026 07:18:44 GMT"),
        ];

        Assert.Equal(fields, RoundTrip(fields));
    }

    // A value long enough that its length does not fit the 7-bit prefix, so the integer
    // continuation path is exercised rather than assumed.
    [Fact]
    public void AValueLongerThanAPrefixCanHoldSurvivesARoundTrip()
    {
        var long_ = new string('a', 400);

        Assert.Equal([("x-long", long_)], RoundTrip([("x-long", long_)]));
    }

    [Fact]
    public void ABufferTooSmallIsReportedRatherThanOverrun()
    {
        var tiny = new byte[4];

        Assert.False(QpackEncoder.TryEncode(
            [("x-correlation-id", "7f3dacad-1fab-4c2e-9d10-0b1e2c3d4e5f")], tiny, out _));
    }

    [Fact]
    public void AnEmptyFieldSectionIsJustThePrefix()
    {
        var buffer = new byte[16];

        Assert.True(QpackEncoder.TryEncode([], buffer, out var written));
        Assert.Equal(2, written);
    }
}
