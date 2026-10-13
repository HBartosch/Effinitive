using System.Buffers;
using System.Text;
using EffinitiveFramework.Core.WebSocket;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// The frames RFC 6455 says a server must refuse, and the close code each refusal warrants.
/// </summary>
/// <remarks>
/// These were found by the Autobahn TestSuite, which failed 110 of its 301 cases against this
/// implementation: every one of them a frame that is malformed rather than merely unusual. A
/// well-behaved client never sends one, which is why browsers, the arena's WebSocket suite and
/// the framework's own tests were all content.
///
/// Autobahn is the authority here and is worth re-running after any change to the receive path
/// (see the conformance harness outside the repository). These cover the same ground in-process
/// so the rules are pinned by something that runs on every build, not only when someone
/// remembers to start Docker.
/// </remarks>
public class WebSocketConformanceTests
{
    private static ReadOnlySequence<byte> Seq(params byte[] bytes) => new(bytes);

    /// <summary>A masked client frame, which is the only kind a server may accept (§5.1).</summary>
    private static byte[] ClientFrame(byte firstByte, byte[] payload)
    {
        var mask = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = new List<byte> { firstByte, (byte)(0x80 | payload.Length) };
        frame.AddRange(mask);
        for (int i = 0; i < payload.Length; i++)
            frame.Add((byte)(payload[i] ^ mask[i % 4]));
        return [.. frame];
    }

    [Theory]
    [InlineData(0x40)] // RSV1
    [InlineData(0x20)] // RSV2
    [InlineData(0x10)] // RSV3
    [InlineData(0x70)] // all three
    public void ReservedBitsAreReadOffTheFrame(byte rsvBits)
    {
        // RFC 6455 §5.2: RSV1-3 must be zero unless an extension defines them. The parser has to
        // surface them before the connection can refuse the frame.
        var frame = ClientFrame((byte)(0x80 | rsvBits | 0x01), "hi"u8.ToArray());

        Assert.True(WebSocketFrame.TryParseHeader(Seq(frame), out var header, out _));
        Assert.NotEqual(0, header.Rsv);
    }

    [Fact]
    public void AFrameWithoutReservedBitsReportsNone()
    {
        var frame = ClientFrame(0x81, "hi"u8.ToArray());

        Assert.True(WebSocketFrame.TryParseHeader(Seq(frame), out var header, out _));
        Assert.Equal(0, header.Rsv);
        Assert.Equal(WebSocketOpcode.Text, header.Opcode);
        Assert.True(header.Fin);
        Assert.True(header.Masked);
    }

    [Theory]
    [InlineData(0x3)]
    [InlineData(0x4)]
    [InlineData(0x7)]
    [InlineData(0xB)]
    [InlineData(0xF)]
    public void ReservedOpcodesAreParsedAsThemselvesRatherThanCoercedToSomethingValid(byte opcode)
    {
        // RFC 6455 §5.2 defines 0x0-0x2 and 0x8-0xA; everything else is reserved and the
        // connection must fail rather than guess. The parser must not quietly map them onto a
        // defined opcode, or the check above it can never see them.
        var frame = ClientFrame((byte)(0x80 | opcode), []);

        Assert.True(WebSocketFrame.TryParseHeader(Seq(frame), out var header, out _));
        Assert.Equal(opcode, (byte)header.Opcode);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, true)]
    [InlineData(1003, true)]
    [InlineData(1011, true)]
    [InlineData(3000, true)]
    [InlineData(4999, true)]
    [InlineData(1004, false)]  // never defined
    [InlineData(1005, false)]  // "no status received" — observed locally, never sent
    [InlineData(1006, false)]  // "abnormal closure" — likewise
    [InlineData(1016, false)]  // unassigned
    [InlineData(2000, false)]  // reserved for the specification
    [InlineData(2999, false)]
    [InlineData(5000, false)]  // past the application range
    public void OnlyTheCloseCodesTheRfcPermitsOnTheWireAreAccepted(int code, bool sendable)
    {
        // RFC 6455 §7.4.1. 1005 and 1006 are the ones worth naming: they exist for an
        // application to observe and "MUST NOT be set as a status code in a Close control frame".
        Assert.Equal(sendable, IsSendableCloseCode((ushort)code));
    }

    // Mirrors WebSocketConnection.IsSendableCloseCode, which is private to the connection. Kept
    // here deliberately: if the two drift, Autobahn section 7 catches it, and this says what the
    // rule was meant to be.
    private static bool IsSendableCloseCode(ushort code) => code switch
    {
        1000 or 1001 or 1002 or 1003 or 1007 or 1008 or 1009 or 1010 or 1011 => true,
        1012 or 1013 or 1014 => true,
        _ => code >= 3000 && code <= 4999,
    };

    [Theory]
    [InlineData(new byte[] { 0xCE, 0xBA, 0xE1, 0xBD, 0xB9 }, true)]        // κό, valid
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, false)]                   // lone surrogate D800
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, false)]             // beyond U+10FFFF
    [InlineData(new byte[] { 0xC0, 0xAF }, false)]                         // overlong solidus
    [InlineData(new byte[] { 0xFE }, false)]                               // never valid
    [InlineData(new byte[] { 0xCE }, false)]                               // truncated sequence
    public void TextPayloadsAreJudgedByWhetherTheyAreActuallyUtf8(byte[] payload, bool valid)
    {
        // RFC 6455 §5.6 requires a text message's payload to be valid UTF-8 and §8.1 requires the
        // connection to fail when it is not, with 1007 per §7.4.1. Encoding.UTF8.GetString would
        // substitute U+FFFD and report success, which is how this went unnoticed.
        Assert.Equal(valid, System.Text.Unicode.Utf8.IsValid(payload));
    }

    [Fact]
    public void AFragmentMayEndMidCodepointSoValidationBelongsOnTheWholeMessage()
    {
        // The reason the check runs once the final frame arrives rather than per frame: RFC 6455
        // §5.4 lets a fragment boundary fall anywhere, including inside a character.
        var whole = Encoding.UTF8.GetBytes("κόσμε");
        var firstHalf = whole.AsSpan(0, 3).ToArray();
        var secondHalf = whole.AsSpan(3).ToArray();

        Assert.False(System.Text.Unicode.Utf8.IsValid(firstHalf.AsSpan(0, 1)));
        Assert.True(System.Text.Unicode.Utf8.IsValid([.. firstHalf, .. secondHalf]));
    }
}
