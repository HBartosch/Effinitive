using System.Net.Sockets;
using System.Security.Cryptography;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core;

public sealed partial class EffinitiveServer
{
    private async Task HandleConnectionAsync(Socket socket, bool isSecure, CancellationToken cancellationToken)
    {
        var connection = _connectionPool.Get();
        _metrics.IncrementConnections();

        // SECURITY: Create timeout token to prevent Slowloris attacks
        using var requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await connection.InitializeAsync(
                socket,
                isSecure,
                _options.TlsOptions.Certificate,
                cancellationToken);

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
                // SECURITY: Reset timeout for each request
                requestTimeoutCts.CancelAfter(_options.RequestTimeout);
                
                // Read request with timeout
                HttpRequest? request;
                try
                {
                    request = await connection.ReadRequestAsync(
                        _options.HeaderTimeout,
                        _options.MaxRequestBodySize,
                        requestTimeoutCts.Token);
                }
                catch (HttpParseException parseEx)
                {
                    // RFC violation detected — send the appropriate error status
                    var errorResponse = new HttpResponse
                    {
                        StatusCode = parseEx.StatusCode,
                        KeepAlive = false,
                        Body = System.Text.Encoding.UTF8.GetBytes(parseEx.Message),
                        ContentType = "text/plain"
                    };
                    await connection.WriteResponseAsync(errorResponse, cancellationToken);
                    break; // Close connection after error
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

                _metrics.IncrementRequests();

                // ── Pre-routing security & compliance checks ──
                var validationResult = ValidateRequest(request);
                if (validationResult.Action != ValidationAction.Continue)
                {
                    if (validationResult.Response != null)
                        await connection.WriteResponseAsync(validationResult.Response, cancellationToken);
                    if (validationResult.Action == ValidationAction.CloseConnection)
                        break;
                    continue; // ValidationAction.SendAndContinue
                }

                // Handle HEAD method: process normally but strip body from response
                bool isHead = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

                // Create response
                var response = new HttpResponse
                {
                    KeepAlive = request.KeepAlive
                };

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
                ApplyConditionalHeaders(request, response, isHead);

                // HEAD responses must not include a body
                if (isHead)
                {
                    // Keep Content-Length set (if any) but remove body
                    if (response.Body != null && response.Body.Length > 0)
                    {
                        response.Headers["Content-Length"] = response.Body.Length.ToString();
                        response.Body = null;
                    }
                }

                // Write response
                await connection.WriteResponseAsync(response, cancellationToken);

                // Check if we should keep connection alive
                if (!response.KeepAlive || !request.KeepAlive)
                    break;

                // Check idle timeout
                var idleDuration = DateTime.UtcNow - connection.LastActivity;
                if (idleDuration > _options.IdleTimeout)
                    break;
            }
        }
        catch (Exception)
        {
            // Connection error - just close
        }
        finally
        {
            // If the connection is in a non-keepalive state, dispose it rather than
            // returning to the pool to ensure the TCP connection is actually closed.
            connection.Dispose();
            _metrics.DecrementConnections();
            Interlocked.Decrement(ref _activeConnections);
        }
    }
}
