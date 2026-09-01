using System.Buffers;
using System.Text;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// TCP is a byte stream with no message boundaries, so a request may arrive in
/// any number of reads split at any offset. RFC 9112 §2.2 frames a request by
/// its own delimiters and nowhere permits a server to infer malformedness from
/// where a read happened to end, so an incomplete request must be carried until
/// it completes, never rejected.
///
/// These split every request shape at every byte offset. The ones that matter
/// are the offsets nobody picks by hand: between the CR and the LF of a header
/// line, mid Content-Length digits, one byte into the terminating CRLF. A parser
/// that rejects those answers 400 to a well-formed request purely because of how
/// the kernel segmented it.
/// </summary>
public class HttpRequestParserFragmentationTests
{
    // The three body framings RFC 9112 defines for a request — none, §6.2
    // Content-Length, and §7.1 chunked — each in more than one spelling, since
    // RFC 9110 §5.1 makes field names case-insensitive and a parser may treat
    // the two casings by different paths.
    public static TheoryData<string> Shapes() => new()
    {
        "GET /resource?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",

        "GET /resource?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\n" +
        "User-Agent: effinitive-tests/1.0\r\nAccept: text/plain\r\n" +
        "Accept-Encoding: identity\r\nConnection: close\r\n\r\n",

        "GET /resource?a=13&b=42 HTTP/1.1\r\nhost: localhost\r\n" +
        "user-agent: effinitive-tests/1.0\r\nconnection: close\r\n\r\n",

        "POST /resource?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\n" +
        "Content-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\n20",

        "POST /resource?a=13&b=42 HTTP/1.1\r\nhost: localhost\r\n" +
        "content-type: text/plain\r\ncontent-length: 2\r\nconnection: close\r\n\r\n20",

        "POST /resource?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\n" +
        "Content-Type: text/plain\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
        "2\r\n20\r\n0\r\n\r\n",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryPrefix_IsIncomplete_NotMalformed(string raw)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);

        // A prefix that already contains the full head parses early: the chunked
        // and streaming paths return before the body arrives, by design. Anything
        // short of that must report "need more data" rather than throw.
        for (int length = 1; length < bytes.Length; length++)
        {
            var buffer = new ReadOnlySequence<byte>(bytes, 0, length);
            var request = new HttpRequest();

            var ex = Record.Exception(
                () => HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _));

            Assert.True(ex is null,
                $"prefix of {length}/{bytes.Length} bytes threw {ex?.GetType().Name}: {ex?.Message}\n" +
                $"  ...{Escape(raw[..length])} >>>SPLIT<<< {Escape(raw[length..])}...");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EverySplit_AcrossTwoSegments_Parses(string raw)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);

        // Once both halves have arrived the pipe holds them as separate segments.
        // The parser must reassemble across that boundary at every offset.
        for (int split = 1; split < bytes.Length; split++)
        {
            var buffer = Segmented(bytes, split);
            var request = new HttpRequest();

            Assert.True(
                HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _),
                $"split at {split}/{bytes.Length} did not parse\n" +
                $"  ...{Escape(raw[..split])} >>>SPLIT<<< {Escape(raw[split..])}...");

            Assert.Equal("/resource?a=13&b=42", request.Path);
            Assert.Equal("HTTP/1.1", request.HttpVersion);
            Assert.True(request.Headers.ContainsKey("Host") || request.Headers.ContainsKey("host"));
        }
    }

    private static ReadOnlySequence<byte> Segmented(byte[] bytes, int split)
    {
        var first = new Segment(bytes.AsMemory(0, split));
        var second = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
