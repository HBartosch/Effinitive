using System.Buffers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

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
        [101] = "HTTP/1.1 101 Switching Protocols\r\n"u8.ToArray(),
        [200] = "HTTP/1.1 200 OK\r\n"u8.ToArray(),
        [201] = "HTTP/1.1 201 Created\r\n"u8.ToArray(),
        [204] = "HTTP/1.1 204 No Content\r\n"u8.ToArray(),
        [400] = "HTTP/1.1 400 Bad Request\r\n"u8.ToArray(),
        [401] = "HTTP/1.1 401 Unauthorized\r\n"u8.ToArray(),
        [403] = "HTTP/1.1 403 Forbidden\r\n"u8.ToArray(),
        [404] = "HTTP/1.1 404 Not Found\r\n"u8.ToArray(),
        [405] = "HTTP/1.1 405 Method Not Allowed\r\n"u8.ToArray(),
        [414] = "HTTP/1.1 414 URI Too Long\r\n"u8.ToArray(),
        [431] = "HTTP/1.1 431 Request Header Fields Too Large\r\n"u8.ToArray(),
        [500] = "HTTP/1.1 500 Internal Server Error\r\n"u8.ToArray(),
        [501] = "HTTP/1.1 501 Not Implemented\r\n"u8.ToArray(),
        [503] = "HTTP/1.1 503 Service Unavailable\r\n"u8.ToArray(),
        [505] = "HTTP/1.1 505 HTTP Version Not Supported\r\n"u8.ToArray(),
    };

    // Tunable for A/B benchmarking of the 200 text/plain keep-alive fast path.
    private const bool EnableHeaderBlockSpecialization = false;

    // Hot-path specialized header blocks for 200 text/plain keep-alive
    private static readonly byte[] HotPathPrefix = "HTTP/1.1 200 OK\r\nDate: "u8.ToArray();
    private static readonly byte[] HotPathMiddle = "\r\nServer: effinitive\r\nContent-Type: text/plain\r\nContent-Length: "u8.ToArray();
    private static readonly byte[] HotPathSuffix = "\r\nConnection: keep-alive\r\n\r\n"u8.ToArray();
    private static readonly byte[] DateHeaderPrefix = "Date: "u8.ToArray();
    private static readonly byte[] ServerHeaderPrefix = "Server: "u8.ToArray();
    private static readonly byte[] ContentTypeHeaderPrefix = "Content-Type: "u8.ToArray();
    private static readonly byte[] ContentLengthHeaderPrefix = "Content-Length: "u8.ToArray();
    private static readonly byte[] TransferEncodingChunked = "Transfer-Encoding: chunked\r\n"u8.ToArray();
    private static readonly byte[] FinalChunk = "0\r\n\r\n"u8.ToArray();

    private static long _cachedDateSecond = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
    private static string _cachedDateValue = DateTime.UtcNow.ToString("R");

    // Thread-local reusable MemoryStream for JSON serialization (uncompressed path).
    [ThreadStatic]
    private static MemoryStream? t_jsonBuffer;

    private static MemoryStream RentJsonBuffer()
    {
        var ms = t_jsonBuffer;
        if (ms != null)
        {
            ms.Position = 0;
            ms.SetLength(0);
            return ms;
        }
        ms = new MemoryStream(1_048_576); // 1MB initial — matches typical large JSON response
        t_jsonBuffer = ms;
        return ms;
    }

    /// <summary>
    /// Write HTTP response to PipeWriter
    /// </summary>
    public static async ValueTask WriteResponseAsync(
        PipeWriter writer,
        HttpResponse response,
        CancellationToken cancellationToken = default,
        bool flush = true)
    {
        // HOT-PATH: 200 OK text/plain keep-alive with no non-standard headers.
        // This bypasses dictionary mutation and header enumeration entirely.
        if (EnableHeaderBlockSpecialization && TryGetHotPathBodyLength(response, out var bodyLength))
        {
            writer.Write(HotPathPrefix);
            WriteAsciiDateValue(writer, GetCachedDateHeaderValue());
            writer.Write(HotPathMiddle);
            WriteAscii(writer, bodyLength.ToString());
            writer.Write(HotPathSuffix);

            // Write body if present
            if (response.Body != null && response.Body.Length > 0)
            {
                writer.Write(response.Body);
            }

            if (flush)
                await writer.FlushAsync(cancellationToken);

            return;
        }

        // COLD-PATH: Non-standard responses
        var headers = response.HeadersOrNull;
        
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

        // 101 Switching Protocols — write headers verbatim, no auto-generated headers.
        // Used for WebSocket upgrade: the caller sets Upgrade, Connection, Sec-WebSocket-Accept.
        if (response.StatusCode == 101)
        {
            if (headers != null)
            {
                foreach (var (name, value) in headers)
                {
                    WriteAscii(writer, name);
                    writer.Write(HeaderSeparator);
                    WriteAscii(writer, value);
                    writer.Write(CrLf);
                }
            }
            writer.Write(CrLf);
            if (flush)
                await writer.FlushAsync(cancellationToken);
            return;
        }

        var dateValue = GetCachedDateHeaderValue();
        if (headers != null && headers.TryGetValue("Date", out var explicitDate) && !string.IsNullOrEmpty(explicitDate))
            dateValue = explicitDate;

        var serverValue = "effinitive";
        if (headers != null && headers.TryGetValue("Server", out var explicitServer) && !string.IsNullOrEmpty(explicitServer))
            serverValue = explicitServer;

        var contentTypeValue = response.ContentType;
        if (string.IsNullOrEmpty(contentTypeValue))
            contentTypeValue = "application/json";

        // Handle streaming responses (SSE, chunked transfer, etc.)
        if (response.IsStreaming && response.StreamHandler != null)
        {
            // Don't add Content-Length for streaming responses.
            // Write standard headers first.
            writer.Write(DateHeaderPrefix);
            WriteAsciiDateValue(writer, dateValue);
            writer.Write(CrLf);

            writer.Write(ServerHeaderPrefix);
            WriteAscii(writer, serverValue);
            writer.Write(CrLf);

            writer.Write(ContentTypeHeaderPrefix);
            WriteAscii(writer, contentTypeValue);
            writer.Write(CrLf);

            // Write custom headers except standard ones already emitted.
            if (headers != null)
            {
                foreach (var (name, value) in headers)
                {
                    if (name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                        continue;

                    WriteAscii(writer, name);
                    writer.Write(HeaderSeparator);
                    WriteAscii(writer, value);
                    writer.Write(CrLf);
                }
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
            await writer.FlushAsync(cancellationToken);

            // Execute stream handler - this will write data directly to the underlying stream
            // Note: We can't use PipeWriter here as streaming needs direct stream access
            return;
        }

        // Write standard headers first
        writer.Write(DateHeaderPrefix);
        WriteAsciiDateValue(writer, dateValue);
        writer.Write(CrLf);

        writer.Write(ServerHeaderPrefix);
        WriteAscii(writer, serverValue);
        writer.Write(CrLf);

        writer.Write(ContentTypeHeaderPrefix);
        WriteAscii(writer, contentTypeValue);
        writer.Write(CrLf);

        // ---- Compressed response path ----
        // Streaming compression with chunked transfer encoding (like Kestrel):
        // GZipStream writes directly to PipeWriter via chunk framing — no
        // intermediate MemoryStream buffer, no extra memcpy.
        if (response.GzipCompressionLevel.HasValue)
        {
            var level = response.GzipCompressionLevel.Value;

            // Get uncompressed body bytes
            ReadOnlySpan<byte> bodySpan;
            if (response.BodyObject != null)
            {
                var jsonMs = RentJsonBuffer();
                JsonSerializer.Serialize(jsonMs, response.BodyObject,
                    response.BodyObject.GetType(), response.BodySerializerOptions);
                bodySpan = jsonMs.GetBuffer().AsSpan(0, (int)jsonMs.Position);
            }
            else
            {
                bodySpan = response.Body ?? ReadOnlySpan<byte>.Empty;
            }

            // Write Transfer-Encoding: chunked instead of Content-Length
            writer.Write(TransferEncodingChunked);

            if (headers != null)
            {
                foreach (var (name, value) in headers)
                {
                    if (IsStandardHeader(name)) continue;
                    WriteAscii(writer, name);
                    writer.Write(HeaderSeparator);
                    WriteAscii(writer, value);
                    writer.Write(CrLf);
                }
            }

            writer.Write(response.KeepAlive ? ConnectionKeepAlive : ConnectionClose);
            writer.Write(CrLf);

            // Stream compressed data directly to PipeWriter with chunk framing
            using (var gz = new GZipStream(new ChunkedPipeWriterStream(writer), level))
            {
                gz.Write(bodySpan);
            }

            // Final chunk marker
            writer.Write(FinalChunk);

            if (flush)
                await writer.FlushAsync(cancellationToken);
            return;
        }

        // ---- Normal (uncompressed) response path ----
        // Materialize deferred body if needed
        response.MaterializeDeferredBody();

        // Content-Length handling (RFC 9110 §6.3.5: no Content-Length for 204)
        if (response.StatusCode != 204)
        {
            string contentLengthValue;
            if (headers != null && headers.TryGetValue("Content-Length", out var explicitLength) && !string.IsNullOrEmpty(explicitLength))
            {
                contentLengthValue = explicitLength;
            }
            else
            {
                contentLengthValue = (response.Body?.Length ?? 0).ToString();
            }

            writer.Write(ContentLengthHeaderPrefix);
            WriteAscii(writer, contentLengthValue);
            writer.Write(CrLf);
        }

        // Write custom headers except standard ones already emitted.
        if (headers != null)
        {
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    continue;

                WriteAscii(writer, name);
                writer.Write(HeaderSeparator);
                WriteAscii(writer, value);
                writer.Write(CrLf);
            }
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

        if (flush)
            await writer.FlushAsync(cancellationToken);
    }

    private static void WriteAscii(PipeWriter writer, string text)
    {
        var span = writer.GetSpan(text.Length);
        var bytesWritten = Encoding.ASCII.GetBytes(text, span);
        writer.Advance(bytesWritten);
    }

    /// <summary>
    /// Write cached RFC 1123 date value directly as ASCII bytes
    /// </summary>
    private static void WriteAsciiDateValue(PipeWriter writer, string dateValue)
    {
        // RFC 1123 dates are always ASCII, safe to encode directly
        var span = writer.GetSpan(dateValue.Length);
        var bytesWritten = Encoding.ASCII.GetBytes(dateValue, span);
        writer.Advance(bytesWritten);
    }

    private static string GetCachedDateHeaderValue()
    {
        var now = DateTime.UtcNow;
        var second = now.Ticks / TimeSpan.TicksPerSecond;
        if (second == Volatile.Read(ref _cachedDateSecond))
            return _cachedDateValue;

        var formatted = now.ToString("R");
        Volatile.Write(ref _cachedDateValue, formatted);
        Volatile.Write(ref _cachedDateSecond, second);
        return formatted;
    }

    private static bool TryGetHotPathBodyLength(HttpResponse response, out int bodyLength)
    {
        bodyLength = response.Body?.Length ?? 0;

        if (response.StatusCode != 200 || !response.KeepAlive || response.IsStreaming)
            return false;

        if (!response.ContentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            return false;

        var headers = response.HeadersOrNull;

        // Allow only Content-Type in custom headers; Date/Server/Content-Length are emitted by the hot path.
        if (headers == null || headers.Count == 0)
            return true;

        if (headers.Count == 1 && headers.ContainsKey("Content-Type"))
            return true;

        return false;
    }

    private static bool IsStandardHeader(string name) =>
        name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stream adapter that writes to PipeWriter with HTTP chunked transfer encoding framing.
    /// GZipStream writes compressed chunks directly to PipeWriter — no intermediate buffer.
    /// </summary>
    private sealed class ChunkedPipeWriterStream : Stream
    {
        private readonly PipeWriter _writer;

        public ChunkedPipeWriterStream(PipeWriter writer) => _writer = writer;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;
            WriteChunkHeader(count);
            _writer.Write(buffer.AsSpan(offset, count));
            _writer.Write(CrLf);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty) return;
            WriteChunkHeader(buffer.Length);
            _writer.Write(buffer);
            _writer.Write(CrLf);
        }

        private void WriteChunkHeader(int size)
        {
            // Write hex chunk size + \r\n directly into PipeWriter
            Span<char> chars = stackalloc char[8];
            size.TryFormat(chars, out int written, "x");
            var span = _writer.GetSpan(written + 2);
            for (int i = 0; i < written; i++)
                span[i] = (byte)chars[i];
            span[written] = (byte)'\r';
            span[written + 1] = (byte)'\n';
            _writer.Advance(written + 2);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
