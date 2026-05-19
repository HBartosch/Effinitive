namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Exception thrown when HTTP request parsing detects an RFC violation.
/// Carries the appropriate HTTP status code (400, 414, 431, 501, 505).
/// </summary>
public sealed class HttpParseException : Exception
{
    public int StatusCode { get; }
    public bool KeepAliveAllowed { get; }

    public HttpParseException(int statusCode, string message, bool keepAliveAllowed = false) : base(message)
    {
        StatusCode = statusCode;
        KeepAliveAllowed = keepAliveAllowed;
    }

    public static HttpParseException BadRequest(string detail) => new(400, detail);
    public static HttpParseException UriTooLong(string detail) => new(414, detail);
    public static HttpParseException HeaderFieldsTooLarge(string detail) => new(431, detail);
    public static HttpParseException NotImplemented(string detail) => new(501, detail);
    public static HttpParseException VersionNotSupported(string detail) => new(505, detail);
}
