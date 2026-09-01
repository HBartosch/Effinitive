using System.Buffers;
using System.Text;
using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// A request that arrives across more than one read must be treated as incomplete,
/// never as malformed. These split every request shape at every byte offset, the
/// same sweep HttpArena's validate-frag.py runs over a live socket.
///
/// The split points that matter are the ones nobody picks by hand: between the CR
/// and the LF of a header line, mid Content-Length digits, one byte into the
/// terminating CRLF. A parser that rejects those answers 400 to a well-formed
/// request purely because of how the kernel happened to segment it.
/// </summary>
public class HttpRequestParserFragmentationTests
{
    // The HTTP/1.1 shapes the arena baseline profile defines, in the spellings
    // that exercise the header, Content-Length and chunked framing paths.
    public static TheoryData<string> Shapes() => new()
    {
        "GET /baseline11?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",

        "GET /baseline11?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\n" +
        "User-Agent: arena-frag/1.0\r\nAccept: text/plain\r\n" +
        "Accept-Encoding: identity\r\nConnection: close\r\n\r\n",

        "GET /baseline11?a=13&b=42 HTTP/1.1\r\nhost: localhost\r\n" +
        "user-agent: arena-frag/1.0\r\nconnection: close\r\n\r\n",

        "POST /baseline11?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\n" +
        "Content-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\n20",

        "POST /baseline11?a=13&b=42 HTTP/1.1\r\nhost: localhost\r\n" +
        "content-type: text/plain\r\ncontent-length: 2\r\nconnection: close\r\n\r\n20",

        "POST /baseline11?a=13&b=42 HTTP/1.1\r\nHost: localhost\r\n" +
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

            Assert.Equal("/baseline11?a=13&b=42", request.Path);
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
