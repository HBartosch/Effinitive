using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace EffinitiveFramework.Core.Transport;

/// <summary>
/// High-performance socket transport with separated read/write loops.
/// 
/// Architecture:
///   Socket ──recv──► SocketReceiver ──► Pipe(Input) ──► PipeReader  [App reads requests]
///   Socket ◄──send── SocketSender  ◄── Pipe(Output) ◄── PipeWriter  [App writes responses]
/// 
/// The DoReceive and DoSend tasks run independently — writes never block reads.
/// Uses pooled SocketAsyncEventArgs for zero-allocation I/O and IOQueue scheduling
/// to batch continuations instead of dispatching each to ThreadPool separately.
/// </summary>
internal sealed class SocketTransportConnection : IAsyncDisposable
{
    private const int MinAllocBufferSize = 65536;

    private readonly Socket _socket;
    private readonly SocketReceiver _receiver;
    private readonly SocketSenderPool _senderPool;
    private readonly IDuplexPipe _transportPipe;
    private readonly bool _waitForData;

    private SocketSender? _sender;
    private Task? _receiveTask;
    private Task? _sendTask;
    private volatile bool _aborted;

    /// <summary>
    /// The application-facing pipe endpoints.
    /// App reads from Input (request data), writes to Output (response data).
    /// </summary>
    public IDuplexPipe Application { get; }

    public SocketTransportConnection(
        Socket socket,
        PipeScheduler ioScheduler,
        SocketSenderPool senderPool,
        PipeOptions inputOptions,
        PipeOptions outputOptions,
        bool waitForData = true)
    {
        _socket = socket;
        _senderPool = senderPool;
        _waitForData = waitForData;

        _receiver = new SocketReceiver(ioScheduler);

        // Create DuplexPipe: transport writes to Input, reads from Output.
        // Application reads from Input, writes to Output.
        Application = DuplexPipe.CreateConnectionPair(inputOptions, outputOptions, out _transportPipe);
    }

    /// <summary>
    /// Start the separated receive and send loops.
    /// </summary>
    public void Start()
    {
        _receiveTask = DoReceiveAsync();
        _sendTask = DoSendAsync();
    }

    /// <summary>
    /// Abort the connection (e.g. on shutdown or error).
    /// </summary>
    public void Abort()
    {
        _aborted = true;
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        _socket.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_receiveTask != null) await _receiveTask;
            if (_sendTask != null) await _sendTask;
        }
        catch { }
        finally
        {
            _receiver.Dispose();
            _sender?.Dispose();
        }
    }

    /// <summary>
    /// Receive loop: reads from socket → writes into transport Input pipe.
    /// The application's PipeReader sees the data.
    /// </summary>
    private async Task DoReceiveAsync()
    {
        var input = _transportPipe.Output; // Transport writes to input pipe

        Exception? error = null;
        try
        {
            while (!_aborted)
            {
                if (_waitForData)
                {
                    // Zero-byte receive: block until data arrives without allocating a buffer.
                    // Idle connections consume zero buffer memory.
                    var waitResult = await _receiver.WaitForDataAsync(_socket);
                    if (waitResult.HasError || _aborted)
                    {
                        error = waitResult.SocketError;
                        break;
                    }
                }

                // Get buffer from the pipe (pool-backed, zero-alloc steady state)
                var buffer = input.GetMemory(MinAllocBufferSize);
                var receiveResult = await _receiver.ReceiveAsync(_socket, buffer);

                if (receiveResult.HasError)
                {
                    error = receiveResult.SocketError;
                    break;
                }

                if (receiveResult.BytesTransferred == 0)
                {
                    // FIN — client closed the connection
                    break;
                }

                input.Advance(receiveResult.BytesTransferred);

                var flushResult = await input.FlushAsync();
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    // Application completed the reader (e.g. during shutdown)
                    break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Socket was disposed, expected during shutdown
        }
        catch (SocketException ex)
        {
            error = ex;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            input.Complete(error);
        }
    }

    /// <summary>
    /// Send loop: reads from transport Output pipe → sends to socket.
    /// The application's PipeWriter feeds data into this loop.
    /// </summary>
    private async Task DoSendAsync()
    {
        var output = _transportPipe.Input; // Transport reads from output pipe

        Exception? error = null;
        try
        {
            while (true)
            {
                var result = await output.ReadAsync();

                if (result.IsCanceled)
                    break;

                var buffer = result.Buffer;

                if (!buffer.IsEmpty)
                {
                    _sender = _senderPool.Rent();

                    // Loop until all bytes are sent — SendAsync may do partial writes
                    var remaining = buffer;
                    while (!remaining.IsEmpty)
                    {
                        var sendResult = await _sender.SendAsync(_socket, remaining);

                        if (sendResult.HasError)
                        {
                            error = sendResult.SocketError;
                            _sender.Dispose();
                            _sender = null;
                            goto SendError;
                        }

                        remaining = remaining.Slice(sendResult.BytesTransferred);
                    }

                    // Return sender to pool — only if no error
                    _senderPool.Return(_sender);
                    _sender = null;
                }

                output.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                    break;
            }
        SendError:;
        }
        catch (ObjectDisposedException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Shutdown(error);
            output.Complete(error);
        }
    }

    private void Shutdown(Exception? error)
    {
        if (_aborted) return;
        _aborted = true;

        try
        {
            if (error != null)
            {
                // Abortive close — send RST
                _socket.Close(timeout: 0);
            }
            else
            {
                // Graceful close — send FIN
                _socket.Shutdown(SocketShutdown.Both);
                _socket.Dispose();
            }
        }
        catch
        {
            // Ignore shutdown errors — we're tearing down anyway
        }
    }
}
