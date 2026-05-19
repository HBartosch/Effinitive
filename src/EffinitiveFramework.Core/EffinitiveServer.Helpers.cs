using System.Text.Json;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;
using EffinitiveFramework.Core.WebSocket;
#if NET10_0_OR_GREATER
using System.Net.Quic;
using System.Net.Security;
using EffinitiveFramework.Core.Http3;
#endif

namespace EffinitiveFramework.Core;

public sealed partial class EffinitiveServer
{
    private static readonly byte[] TinyTextOk = "ok"u8.ToArray();
    private static readonly byte[] TinyTextEmpty = Array.Empty<byte>();
    private static readonly byte[] TinyTextTrue = "true"u8.ToArray();
    private static readonly byte[] TinyTextFalse = "false"u8.ToArray();

    private static object? ConvertRouteParam(string value, Type targetType)
    {
        // Handle nullable value types by converting using the underlying type
        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var isNullable = nonNullableType != targetType;

        try
        {
            if (nonNullableType == typeof(string)) return value;
            if (nonNullableType == typeof(int))    return int.Parse(value);
            if (nonNullableType == typeof(long))   return long.Parse(value);
            if (nonNullableType == typeof(Guid))   return Guid.Parse(value);

            return Convert.ChangeType(value, nonNullableType);
        }
        catch
        {
            if (isNullable)
            {
                // For nullable targets, treat conversion failures as null values
                return null;
            }

            // Preserve existing behavior for non-nullable types
            throw;
        }
    }

    /// <summary>
    /// RFC 9110 §8.8.3.2 Weak comparison: two entity-tags are equivalent
    /// if their opaque-tags match character-by-character, regardless of W/ prefix.
    /// </summary>
    internal static bool WeakETagMatch(string ifNoneMatch, string responseEtag)
    {
        var inm = ifNoneMatch.AsSpan().Trim();

        // Wildcard matches any existing resource
        if (inm is "*")
            return true;

        // Get opaque tag from response ETag (strip W/ prefix)
        var responseOpaque = responseEtag.AsSpan().Trim();
        if (responseOpaque.StartsWith("W/", StringComparison.Ordinal))
            responseOpaque = responseOpaque[2..];

        // Must be a quoted string to be valid
        if (responseOpaque.Length < 2 || responseOpaque[0] != '"' || responseOpaque[^1] != '"')
            return false;

        // Parse comma-separated ETags in If-None-Match
        foreach (var segment in ifNoneMatch.Split(','))
        {
            var candidate = segment.AsSpan().Trim();
            if (candidate.Length == 0) continue;

            // Strip W/ prefix for weak comparison
            if (candidate.StartsWith("W/", StringComparison.Ordinal))
                candidate = candidate[2..];

            // Must be a properly quoted ETag to match
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
            {
                if (candidate.SequenceEqual(responseOpaque))
                    return true;
            }
        }

        return false;
    }

    private void SerializeResponse(HttpResponse response, object? responseObj, string contentType)
    {
        if (responseObj is Http.RawResponse raw)
        {
            response.StatusCode = raw.StatusCode;
            response.ContentType = raw.ContentType;
            response.Body = raw.Body;
            if (raw.Headers != null)
            {
                foreach (var h in raw.Headers)
                    response.Headers[h.Key] = h.Value;
            }
            return;
        }

        if (responseObj != null)
        {
            response.StatusCode = 200;
            response.ContentType = contentType;

            if (responseObj is byte[] bytes)
            {
                // Pre-serialized bytes — use directly with the endpoint's content type.
                response.Body = bytes;
            }
            else if (contentType == "text/plain")
            {
                if (responseObj is string str)
                {
                    response.Body = GetTinyTextBodyOrEncode(str);
                }
                else
                {
                    response.Body = System.Text.Encoding.UTF8.GetBytes(responseObj.ToString() ?? "");
                }
            }
            else
            {
                // Defer serialization — store the object so the writer can
                // serialize + compress in one pipeline with pooled buffers.
                response.BodyObject = responseObj;
                response.BodySerializerOptions = _options.JsonOptions;
            }
        }
        else
        {
            response.StatusCode = 200;
            response.Body = Array.Empty<byte>();
            response.ContentType = "text/plain";
        }
    }

    private static byte[] GetTinyTextBodyOrEncode(string value)
    {
        if (value.Length == 0)
            return TinyTextEmpty;
        if (value.Length == 2 && value[0] == 'o' && value[1] == 'k')
            return TinyTextOk;
        if (value.Length == 4 && value[0] == 't' && value[1] == 'r' && value[2] == 'u' && value[3] == 'e')
            return TinyTextTrue;
        if (value.Length == 5 && value[0] == 'f' && value[1] == 'a' && value[2] == 'l' && value[3] == 's' && value[4] == 'e')
            return TinyTextFalse;

        return System.Text.Encoding.UTF8.GetBytes(value);
    }

    private async Task HandleErrorAsync(Exception exception, HttpRequest request, HttpResponse response)
    {
        response.StatusCode = 500;
        var problemDetails = ProblemDetails.FromException(exception, 500, request.Path);
        response.Body = JsonSerializer.SerializeToUtf8Bytes(problemDetails, _options.JsonOptions);
        response.ContentType = "application/problem+json";

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle HTTP/2 connection
    /// </summary>
    private async Task HandleHttp2ConnectionAsync(HttpConnection connection, CancellationToken cancellationToken)
    {
        // Create request handler that routes through the framework
        async Task<HttpResponse> RequestHandler(HttpRequest request)
        {
            var response = new HttpResponse();
            
            try
            {
                await HandleRequestAsync(request, response, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync(ex, request, response);
            }
            
            return response;
        }
        
        var http2Connection = new Http2Connection(connection.Stream!, RequestHandler);
        
        try
        {
            await http2Connection.ProcessAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (!_isProduction)
                Console.WriteLine($"HTTP/2 connection error: {ex.Message}");
        }
        finally
        {
            await http2Connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Detect WebSocket upgrade request per RFC 6455 §4.2.1.
    /// </summary>
    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return false;

        var headers = request.Headers;
        if (headers == null)
            return false;

        // Must have Connection: Upgrade and Upgrade: websocket
        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!headers.TryGetValue("Connection", out var connection))
            return false;

        // Connection header may contain multiple values (e.g., "keep-alive, Upgrade")
        bool hasUpgrade = false;
        foreach (var part in connection.Split(','))
        {
            if (part.AsSpan().Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                hasUpgrade = true;
                break;
            }
        }

        if (!hasUpgrade)
            return false;

        // Must have Sec-WebSocket-Key
        return headers.ContainsKey("Sec-WebSocket-Key");
    }

    /// <summary>
    /// Perform WebSocket upgrade handshake and hand off to the WebSocket handler.
    /// </summary>
    private async Task HandleWebSocketUpgradeAsync(
        HttpConnection connection,
        HttpRequest request,
        Func<WebSocketConnection, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var clientKey = request.Headers!["Sec-WebSocket-Key"];
        var acceptKey = WebSocketConnection.ComputeAcceptKey(clientKey);

        // Build 101 Switching Protocols response
        var response = new HttpResponse
        {
            StatusCode = 101,
            KeepAlive = false,
            ContentType = null
        };
        response.Headers["Upgrade"] = "websocket";
        response.Headers["Connection"] = "Upgrade";
        response.Headers["Sec-WebSocket-Accept"] = acceptKey;

        // Send the upgrade response
        await connection.WriteResponseAsync(response, cancellationToken, flush: true);

        // The underlying stream is now a WebSocket connection
        var stream = connection.GetOrCreateStream();
        if (stream == null)
        {
            if (!_isProduction)
                Console.WriteLine("WebSocket upgrade failed: no underlying stream available");
            return;
        }

        await using var wsConnection = new WebSocketConnection(stream);

        try
        {
            await handler(wsConnection, cancellationToken);
        }
        catch (Exception ex)
        {
            if (!_isProduction)
                Console.WriteLine($"WebSocket handler error: {ex.Message}");
        }
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// Accept HTTP/3 connections via QUIC.
    /// </summary>
    private async Task AcceptHttp3ConnectionsAsync(CancellationToken cancellationToken)
    {
        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, _options.HttpsPort),
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0x0102, // H3_INTERNAL_ERROR
                DefaultCloseErrorCode = 0x0100,  // H3_NO_ERROR
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _options.TlsOptions.Certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 }
                }
            })
        }, cancellationToken);

        if (!_isProduction)
            Console.WriteLine($"HTTP/3 (QUIC) listening on port {_options.HttpsPort}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QuicConnection quicConnection;
                try
                {
                    quicConnection = await listener.AcceptConnectionAsync(cancellationToken);
                }
                catch (QuicException ex)
                {
                    if (!_isProduction)
                        Console.WriteLine($"HTTP/3 accept error: {ex.Message}");
                    continue;
                }

                async Task<HttpResponse> RequestHandler(HttpRequest request)
                {
                    var response = new HttpResponse();
                    try
                    {
                        await HandleRequestAsync(request, response, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await HandleErrorAsync(ex, request, response);
                    }
                    return response;
                }

                var h3Connection = new Http3Connection(quicConnection, RequestHandler);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await h3Connection.ProcessAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        if (!_isProduction)
                            Console.WriteLine($"HTTP/3 connection error: {ex.Message}");
                    }
                    finally
                    {
                        await h3Connection.DisposeAsync();
                    }
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await listener.DisposeAsync();
        }
    }
#endif
}

/// <summary>
/// No-op service provider for when DI is not configured
/// </summary>
internal sealed class NoOpServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
