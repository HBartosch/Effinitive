using System.Net.Sockets;
using System.Security.Cryptography;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Transport;
using EffinitiveFramework.Core.WebSocket;

namespace EffinitiveFramework.Core;

public sealed partial class EffinitiveServer
{
    private async Task HandleConnectionAsync(
        Socket socket, bool isSecure, CancellationToken cancellationToken,
        IOQueue ioQueue, SocketSenderPool senderPool)
    {
        var connection = _connectionPool.Get();
        _metrics.IncrementConnections();

        try
        {
            await connection.InitializeAsync(
                socket,
                isSecure,
                _options.TlsOptions.Certificate,
                cancellationToken,
                ioQueue,
                senderPool);

            // Check if HTTP/2 was negotiated
            if (connection.NegotiatedProtocol == "h2")
            {
                if (!_isProduction)
                    Console.WriteLine("HTTP/2 connection detected via ALPN");
                await HandleHttp2ConnectionAsync(connection, cancellationToken);
                return;
            }

            // Handle HTTP/1.1 requests (keep-alive)
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for the next request (async I/O with header timeout)
                HttpRequest? request;
                try
                {
                    request = await connection.ReadRequestAsync(
                        _options.HeaderTimeout,
                        _options.MaxRequestBodySize,
                        cancellationToken);
                }
                catch (HttpParseException parseEx)
                {
                    // RFC violation detected — send the appropriate error status
                    var errorResponse = new HttpResponse
                    {
                        StatusCode = parseEx.StatusCode,
                        KeepAlive = parseEx.KeepAliveAllowed,
                        Body = System.Text.Encoding.UTF8.GetBytes(parseEx.Message),
                        ContentType = "text/plain"
                    };
                    await connection.WriteResponseAsync(errorResponse, cancellationToken);
                    if (!parseEx.KeepAliveAllowed)
                        break;
                    continue;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds maximum allowed size"))
                {
                    var errorResponse = new HttpResponse
                    {
                        StatusCode = 413,
                        KeepAlive = false,
                        Body = System.Text.Encoding.UTF8.GetBytes("Payload too large"),
                        ContentType = "text/plain"
                    };
                    await connection.WriteResponseAsync(errorResponse, cancellationToken);
                    break;
                }

                if (request == null)
                    break; // Connection closed or timeout

                // Process this request and any others already in the pipe buffer (pipelining).
                // All responses are written without flushing and batched into a single flush at
                // the end of the loop, collapsing N TCP writes into 1.
                bool keepAlive = true;
                var response = new HttpResponse();
                while (request != null)
                {
                    // Attach streaming body reader for large bodies (> 1 MB threshold)
                    if (request.BodyDeferred)
                        request.BodyStream = connection.CreateBodyStream(request.ContentLength);

                    _metrics.IncrementRequests();

                    // ── Pre-routing security & compliance checks ──
                    var validationResult = ValidateRequest(request);
                    if (validationResult.Action != ValidationAction.Continue)
                    {
                        if (validationResult.Response != null)
                            await connection.WriteResponseAsync(validationResult.Response, cancellationToken, flush: false);
                        if (validationResult.Action == ValidationAction.CloseConnection)
                        {
                            keepAlive = false;
                            break;
                        }
                        // SendAndContinue — try next queued request
                        request = connection.TryParseQueuedRequest(_options.MaxRequestBodySize);
                        continue;
                    }

                    // Handle HEAD method: process normally but strip body from response
                    bool isHead = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

                    // ── WebSocket upgrade detection ──
                    if (IsWebSocketUpgrade(request))
                    {
                        var wsHandler = _router.FindWebSocketRoute(request.Path.AsSpan());
                        if (wsHandler != null)
                        {
                            await HandleWebSocketUpgradeAsync(connection, request, wsHandler, cancellationToken);
                            keepAlive = false; // WebSocket took over the connection
                            break;
                        }
                        // No WebSocket handler — fall through to 404
                    }

                    // Reuse response object per connection loop iteration to reduce allocations
                    response.Reset();
                    response.KeepAlive = request.KeepAlive;

                    try
                    {
                        // Route and handle request
                        await HandleRequestAsync(request, response, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // Return error response
                        await HandleErrorAsync(ex, request, response);
                    }

                    // Apply conditional response headers (ETag, Last-Modified, 304)
                    // Skip for compressed responses — ETag hashing the body is wasted CPU
                    // since the compressed output differs per request anyway.
                    if (!response.GzipCompressionLevel.HasValue)
                        ApplyConditionalHeaders(request, response, isHead);

                    // HEAD responses must not include a body
                    if (isHead)
                    {
                        if (response.Body != null && response.Body.Length > 0)
                        {
                            response.Headers["Content-Length"] = response.Body.Length.ToString();
                            response.Body = null;
                        }
                    }

                    // Flush immediately when this response ends the connection so the client
                    // sees the final bytes before teardown. Keep-alive responses continue to batch.
                    var flushResponse = !response.KeepAlive || !request.KeepAlive;
                    await connection.WriteResponseAsync(response, cancellationToken, flush: flushResponse);

                    // Drain any unread streamed body bytes so the next request can be read
                    if (request.BodyStream is PipeReaderBodyStream pbs)
                        await pbs.DrainAsync(cancellationToken);

                    if (!response.KeepAlive || !request.KeepAlive)
                    {
                        keepAlive = false;
                        break;
                    }

                    // Check idle timeout
                    var idleDuration = DateTime.UtcNow - connection.LastActivity;
                    if (idleDuration > _options.IdleTimeout)
                    {
                        keepAlive = false;
                        break;
                    }

                    // Greedily parse the next request from already-buffered data — no I/O wait
                    try
                    {
                        request = connection.TryParseQueuedRequest(_options.MaxRequestBodySize);
                    }
                    catch (HttpParseException parseEx)
                    {
                        var errResp = new HttpResponse
                        {
                            StatusCode = parseEx.StatusCode,
                            KeepAlive = false,
                            Body = System.Text.Encoding.UTF8.GetBytes(parseEx.Message),
                            ContentType = "text/plain"
                        };
                        await connection.WriteResponseAsync(errResp, cancellationToken, flush: false);
                        keepAlive = false;
                        break;
                    }
                }

                // Flush all batched responses in a single write
                await connection.FlushAsync(cancellationToken);

                if (!keepAlive)
                {
                    await connection.CloseGracefullyAsync();
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Connection error - just close
        }
        finally
        {
            // Async dispose to avoid blocking ThreadPool threads while transport tasks complete.
            await connection.DisposeAsync();
            _metrics.DecrementConnections();
            Interlocked.Decrement(ref _activeConnections);
        }
    }
}
