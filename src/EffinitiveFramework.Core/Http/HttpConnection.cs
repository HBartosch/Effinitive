using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.ObjectPool;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Represents a single HTTP connection with pipeline support
/// </summary>
public sealed class HttpConnection : IDisposable
{
    private Socket? _socket;
    private Stream? _stream;
    private PipeReader? _reader;
    private PipeWriter? _writer;
    private bool _isSecure;
    private CancellationTokenSource? _timeoutCts;

    public bool IsConnected => _socket?.Connected ?? false;
    public DateTime LastActivity { get; private set; }
    public string? NegotiatedProtocol { get; private set; }
    public Stream? Stream => _stream;

    /// <summary>
    /// Initialize connection with a socket
    /// </summary>
    public async Task InitializeAsync(
        Socket socket,
        bool isSecure,
        X509Certificate2? certificate,
        CancellationToken cancellationToken)
    {
        _socket = socket;
        _isSecure = isSecure;
        LastActivity = DateTime.UtcNow;

        if (isSecure && certificate != null)
        {
            // Wrap in SSL stream with ALPN for HTTP/2 negotiation
            var networkStream = new NetworkStream(_socket, ownsSocket: false);
            var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            
            var sslOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                }
            };
            
            await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);
            
            // Store negotiated protocol
            NegotiatedProtocol = sslStream.NegotiatedApplicationProtocol.Protocol.Length > 0
                ? System.Text.Encoding.ASCII.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.Span)
                : "http/1.1";

            _stream = sslStream;
        }
        else
        {
            _stream = new NetworkStream(_socket, ownsSocket: false);
        }

        // Create pipelines with 4KB initial buffer
        var pipeOptions = new PipeOptions(
            minimumSegmentSize: 4096,
            pauseWriterThreshold: 65536,
            resumeWriterThreshold: 32768,
            useSynchronizationContext: false);

        _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: false));
        _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: false));
    }

    /// <summary>
    /// Read and parse HTTP request
    /// </summary>
    public async ValueTask<HttpRequest?> ReadRequestAsync(
        TimeSpan headerTimeout,
        int maxBodySize,
        CancellationToken cancellationToken)
    {
        if (_reader == null)
            return null;

        _timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timeoutCts.CancelAfter(headerTimeout);

        try
        {
            var request = new HttpRequest();

            while (true)
            {
                var result = await _reader.ReadAsync(_timeoutCts.Token);
                var buffer = result.Buffer;

                if (HttpRequestParser.TryParseRequest(
                    ref buffer,
                    request,
                    out var consumed,
                    out _,
                    maxBodySize))
                {
                    _reader.AdvanceTo(consumed);
                    LastActivity = DateTime.UtcNow;
                    return request;
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    return null; // Connection closed
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds maximum allowed size"))
        {
            // Payload Too Large - return null to close connection
            return null;
        }
        catch (OperationCanceledException)
        {
            // Timeout
            return null;
        }
        finally
        {
            _timeoutCts?.Dispose();
            _timeoutCts = null;
        }
    }

    /// <summary>
    /// Write HTTP response
    /// </summary>
    public async ValueTask WriteResponseAsync(
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (_writer == null)
            return;

        await HttpResponseWriter.WriteResponseAsync(_writer, response, cancellationToken);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Reset connection for reuse in pool
    /// </summary>
    public void Reset()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;
        // Keep socket/stream/pipelines for reuse
    }

    /// <summary>
    /// Dispose the connection
    /// </summary>
    public void Dispose()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _reader?.Complete();
        _writer?.Complete();
        _stream?.Dispose();
        _socket?.Dispose();
    }
}

/// <summary>
/// Object pool policy for HttpConnection
/// </summary>
public sealed class HttpConnectionPoolPolicy : IPooledObjectPolicy<HttpConnection>
{
    public HttpConnection Create()
    {
        return new HttpConnection();
    }

    public bool Return(HttpConnection obj)
    {
        if (!obj.IsConnected)
        {
            obj.Dispose();
            return false; // Don't return to pool
        }

        obj.Reset();
        return true; // Return to pool for reuse
    }
}
