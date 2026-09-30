using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using EffinitiveFramework.Core.WebSocket;

namespace EffinitiveFramework.Benchmarks;

/// <summary>
/// Cost of echoing one WebSocket message, with the network taken out.
/// </summary>
/// <remarks>
/// A frame is written into a pipe the connection reads from, the message is
/// received and echoed, and the reply is drained from the pipe it writes to.
/// What is left is the framing, the copies and the awaits, which is what a
/// throughput figure at one message in flight per connection is made of.
///
/// Run: dotnet run -c Release -- --filter *WebSocketEcho*
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class WebSocketEchoBenchmarks
{
    private Pipe _inbound = null!;
    private Pipe _outbound = null!;
    private WebSocketConnection _connection = null!;
    private byte[] _clientFrame = null!;
    private Pipe _ctlIn = null!;
    private Pipe _ctlOut = null!;
    private byte[] _reply = null!;

    [Params(64, 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _inbound = new Pipe();
        _outbound = new Pipe();
        _connection = new WebSocketConnection(_inbound.Reader, _outbound.Writer);
        _clientFrame = BuildMaskedClientFrame(PayloadSize);
        _ctlIn = new Pipe();
        _ctlOut = new Pipe();
        _reply = new byte[PayloadSize + 2];
    }

    /// <summary>One message in, one message out: the echo-ws shape.</summary>
    [Benchmark(Description = "Receive + echo one message")]
    public async Task EchoOne()
    {
        await _inbound.Writer.WriteAsync(_clientFrame);

        var message = await _connection.ReceiveAsync();
        await _connection.SendAsync(message!.Value.Data, message.Value.Type);

        // Drain the reply so the pipe does not fill across iterations.
        var read = await _outbound.Reader.ReadAsync();
        _outbound.Reader.AdvanceTo(read.Buffer.End);
    }

    /// <summary>
    /// The receive half alone, to apportion the cost between parsing the
    /// inbound frame and writing the outbound one.
    /// </summary>
    [Benchmark(Description = "Receive only")]
    public async Task ReceiveOnly()
    {
        await _inbound.Writer.WriteAsync(_clientFrame);
        _ = await _connection.ReceiveAsync();
    }


    /// <summary>
    /// The same echo, but with the receive already waiting when the frame
    /// arrives, which is what happens on a real connection carrying one
    /// message at a time: the read cannot complete synchronously, so the
    /// continuation has to be scheduled.
    /// </summary>
    [Benchmark(Description = "Echo, receive pending when frame arrives")]
    public async Task EchoAsyncCompletion()
    {
        var receive = _connection.ReceiveAsync();
        await _inbound.Writer.WriteAsync(_clientFrame);

        var message = await receive;
        await _connection.SendAsync(message!.Value.Data, message.Value.Type);

        var read = await _outbound.Reader.ReadAsync();
        _outbound.Reader.AdvanceTo(read.Buffer.End);
    }


    /// <summary>
    /// The same pipe traffic and the same pending-read pattern with no
    /// WebSocket layer at all: a read that has to wait, a write, and a drain.
    /// Whatever this costs is the floor; the difference against the echo above
    /// is what the framing layer adds.
    /// </summary>
    [Benchmark(Description = "Control: bare pipes, read pending when data arrives")]
    public async Task RawPipeAsyncCompletion()
    {
        var pending = _ctlIn.Reader.ReadAsync();
        await _ctlIn.Writer.WriteAsync(_clientFrame);

        var result = await pending;
        _ctlIn.Reader.AdvanceTo(result.Buffer.End);

        await _ctlOut.Writer.WriteAsync(_reply);
        var back = await _ctlOut.Reader.ReadAsync();
        _ctlOut.Reader.AdvanceTo(back.Buffer.End);
    }


    /// <summary>
    /// The receive half alone on the pending path, to apportion the cost
    /// between suspending inside ReceiveAsync and everything after it.
    /// </summary>
    [Benchmark(Description = "Receive only, pending when frame arrives")]
    public async Task ReceiveOnlyAsyncCompletion()
    {
        var receive = _connection.ReceiveAsync();
        await _inbound.Writer.WriteAsync(_clientFrame);
        _ = await receive;
    }

    /// <summary>RFC 6455 §5.3: frames from a client are masked.</summary>
    private static byte[] BuildMaskedClientFrame(int payloadSize)
    {
        var payload = new byte[payloadSize];
        new Random(42).NextBytes(payload);

        var mask = new byte[] { 0x37, 0xfa, 0x21, 0x3d };
        var header = new List<byte> { 0x82 }; // FIN + binary

        if (payloadSize < 126)
        {
            header.Add((byte)(0x80 | payloadSize));
        }
        else
        {
            header.Add(0x80 | 126);
            var len = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)payloadSize);
            header.AddRange(len);
        }
        header.AddRange(mask);

        var masked = new byte[payloadSize];
        for (var i = 0; i < payloadSize; i++)
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);

        return [.. header, .. masked];
    }
}
