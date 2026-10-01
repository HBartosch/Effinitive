using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EffinitiveFramework.Tests.Support;

/// <summary>
/// One parsed HTTP/1.1 response: the status line, the field lines, and exactly the content the
/// framing said belonged to it.
/// </summary>
public sealed record RawResponse(string StatusLine, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public int StatusCode =>
        int.TryParse(StatusLine.Split(' ') is { Length: > 1 } parts ? parts[1] : "0", out var code) ? code : 0;

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>
/// A socket that reads responses the way a client has to: by framing them.
/// </summary>
/// <remarks>
/// A test that reads "whatever arrived" and matches substrings against it depends on how the
/// kernel happened to segment the stream. Read the headers, then exactly the content the framing
/// describes, and the next response starts where this one ended no matter how it was split. That
/// matters most for the keep-alive tests, where stopping early leaves content in the buffer that
/// the following exchange then reads as its own status line.
///
/// Framing follows RFC 9112 §6: a response to HEAD and any 1xx, 204 or 304 has no content
/// regardless of what the fields say, otherwise Content-Length gives the length, and a response
/// with neither runs to end of stream.
/// </remarks>
public sealed class RawHttpClient : IDisposable
{
    private readonly Socket _socket;
    private readonly List<byte> _buffered = new();

    public RawHttpClient(int port, int receiveTimeoutMs = 10000)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = receiveTimeoutMs
        };
        _socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
    }

    public void Send(string raw) => _socket.Send(Encoding.ASCII.GetBytes(raw));

    public RawResponse Exchange(string raw, bool headRequest = false)
    {
        Send(raw);
        return ReadResponse(headRequest);
    }

    public RawResponse ReadResponse(bool headRequest = false)
    {
        var headerBytes = ReadUntilHeadersEnd();
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var lines = headerText.Split("\r\n");
        var statusLine = lines[0];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) break;
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int status = int.TryParse(statusLine.Split(' ') is { Length: > 1 } p ? p[1] : "0", out var c) ? c : 0;
        bool bodyForbidden = headRequest || status is 204 or 304 || (status >= 100 && status < 200);

        string body = string.Empty;
        if (!bodyForbidden && headers.TryGetValue("Content-Length", out var lengthText)
            && int.TryParse(lengthText, out var length) && length > 0)
        {
            body = Encoding.ASCII.GetString(ReadExactly(length));
        }

        return new RawResponse(statusLine, headers, body);
    }

    /// <summary>
    /// Anything the peer sent beyond the responses already framed, which should be nothing.
    /// </summary>
    /// <remarks>
    /// Waits briefly first, so that content sent under a status that forbids it is caught here
    /// rather than left to be misread as the next response.
    /// </remarks>
    public string ReadUnexpectedTrailingBytes(int settleMs = 150)
    {
        Thread.Sleep(settleMs);

        var extra = new List<byte>(_buffered);
        _buffered.Clear();

        var buffer = new byte[8192];
        while (_socket.Available > 0)
        {
            int read = _socket.Receive(buffer);
            if (read == 0) break;
            extra.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return Encoding.ASCII.GetString(extra.ToArray());
    }

    private byte[] ReadUntilHeadersEnd()
    {
        while (true)
        {
            int end = IndexOfHeaderTerminator();
            if (end >= 0)
            {
                var headerBytes = _buffered.GetRange(0, end + 4).ToArray();
                _buffered.RemoveRange(0, end + 4);
                return headerBytes;
            }

            if (!Fill())
                throw new IOException("connection closed before the response headers completed: "
                    + Encoding.ASCII.GetString(_buffered.ToArray()));
        }
    }

    private int IndexOfHeaderTerminator()
    {
        for (int i = 0; i + 3 < _buffered.Count; i++)
        {
            if (_buffered[i] == (byte)'\r' && _buffered[i + 1] == (byte)'\n'
                && _buffered[i + 2] == (byte)'\r' && _buffered[i + 3] == (byte)'\n')
                return i;
        }

        return -1;
    }

    private byte[] ReadExactly(int count)
    {
        while (_buffered.Count < count)
        {
            if (!Fill())
                throw new IOException($"connection closed after {_buffered.Count} of {count} content bytes");
        }

        var bytes = _buffered.GetRange(0, count).ToArray();
        _buffered.RemoveRange(0, count);
        return bytes;
    }

    private bool Fill()
    {
        var buffer = new byte[16 * 1024];
        int read = _socket.Receive(buffer);
        if (read == 0) return false;

        _buffered.AddRange(buffer.AsSpan(0, read).ToArray());
        return true;
    }

    public void Dispose() => _socket.Dispose();
}
