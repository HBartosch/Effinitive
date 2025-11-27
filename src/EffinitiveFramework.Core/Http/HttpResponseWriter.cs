using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace EffinitiveFramework.Core.Http;

/// <summary>
/// High-performance HTTP response writer using PipeWriter
/// </summary>
public static class HttpResponseWriter
{
    private static readonly byte[] Http11 = "HTTP/1.1 "u8.ToArray();
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] HeaderSeparator = ": "u8.ToArray();
    private static readonly byte[] ConnectionKeepAlive = "Connection: keep-alive\r\n"u8.ToArray();
    private static readonly byte[] ConnectionClose = "Connection: close\r\n"u8.ToArray();

    // Cached status lines for common responses
    private static readonly Dictionary<int, byte[]> CachedStatusLines = new()
    {
        [200] = "HTTP/1.1 200 OK\r\n"u8.ToArray(),
        [201] = "HTTP/1.1 201 Created\r\n"u8.ToArray(),
        [204] = "HTTP/1.1 204 No Content\r\n"u8.ToArray(),
        [400] = "HTTP/1.1 400 Bad Request\r\n"u8.ToArray(),
        [401] = "HTTP/1.1 401 Unauthorized\r\n"u8.ToArray(),
        [403] = "HTTP/1.1 403 Forbidden\r\n"u8.ToArray(),
        [404] = "HTTP/1.1 404 Not Found\r\n"u8.ToArray(),
        [405] = "HTTP/1.1 405 Method Not Allowed\r\n"u8.ToArray(),
        [500] = "HTTP/1.1 500 Internal Server Error\r\n"u8.ToArray(),
        [503] = "HTTP/1.1 503 Service Unavailable\r\n"u8.ToArray(),
    };

    /// <summary>
    /// Write HTTP response to PipeWriter
    /// </summary>
    public static async ValueTask WriteResponseAsync(
        PipeWriter writer,
        HttpResponse response,
        CancellationToken cancellationToken = default)
    {
        // Write status line (use cached if available)
        if (CachedStatusLines.TryGetValue(response.StatusCode, out var cachedStatusLine))
        {
            writer.Write(cachedStatusLine);
        }
        else
        {
            writer.Write(Http11);
            WriteAscii(writer, response.StatusCode.ToString());
            writer.Write(CrLf);
        }

        // Ensure Content-Type header exists
        if (!response.Headers.ContainsKey("Content-Type"))
        {
            response.Headers["Content-Type"] = response.ContentType;
        }

        // Add Content-Length if body exists
        if (response.Body != null && response.Body.Length > 0)
        {
            response.Headers["Content-Length"] = response.Body.Length.ToString();
        }
        else if (!response.Headers.ContainsKey("Content-Length"))
        {
            response.Headers["Content-Length"] = "0";
        }

        // Write headers
        foreach (var (name, value) in response.Headers)
        {
            WriteAscii(writer, name);
            writer.Write(HeaderSeparator);
            WriteAscii(writer, value);
            writer.Write(CrLf);
        }

        // Write Connection header
        if (response.KeepAlive)
        {
            writer.Write(ConnectionKeepAlive);
        }
        else
        {
            writer.Write(ConnectionClose);
        }

        // End headers
        writer.Write(CrLf);

        // Write body if present
        if (response.Body != null && response.Body.Length > 0)
        {
            writer.Write(response.Body);
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static void WriteAscii(PipeWriter writer, string text)
    {
        var span = writer.GetSpan(text.Length);
        var bytesWritten = Encoding.ASCII.GetBytes(text, span);
        writer.Advance(bytesWritten);
    }
}
