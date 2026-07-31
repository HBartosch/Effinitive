namespace EffinitiveFramework.Core.Http;

public static class HeaderNames
{
    public const string Accept = "Accept";
    public const string AcceptEncoding = "Accept-Encoding";
    public const string AcceptRanges = "Accept-Ranges";
    public const string Age = "Age";
    public const string Allow = "Allow";
    public const string Authorization = "Authorization";
    public const string CacheControl = "Cache-Control";
    public const string Connection = "Connection";
    public const string ContentEncoding = "Content-Encoding";
    public const string ContentLength = "Content-Length";
    public const string ContentRange = "Content-Range";
    public const string ContentType = "Content-Type";
    public const string ETag = "ETag";
    public const string Expect = "Expect";
    public const string Host = "Host";
    public const string IfModifiedSince = "If-Modified-Since";
    public const string IfNoneMatch = "If-None-Match";
    public const string IfRange = "If-Range";
    public const string LastModified = "Last-Modified";
    public const string Pragma = "Pragma";
    public const string Range = "Range";
    public const string RetryAfter = "Retry-After";
    public const string SecWebSocketAccept = "Sec-WebSocket-Accept";
    public const string SetCookie = "Set-Cookie";
    public const string SecWebSocketKey = "Sec-WebSocket-Key";
    public const string Server = "Server";
    public const string TransferEncoding = "Transfer-Encoding";
    public const string Upgrade = "Upgrade";
    public const string Vary = "Vary";
    public const string XForwardedFor = "X-Forwarded-For";

    // The de-facto rate-limit headers. The IETF draft drops the X- prefix, but the prefixed forms are
    // what clients and gateways actually look for today.
    public const string XRateLimitLimit = "X-RateLimit-Limit";
    public const string XRateLimitRemaining = "X-RateLimit-Remaining";
    public const string XRateLimitReset = "X-RateLimit-Reset";
}

public static class MediaTypes
{
    public const string ApplicationJavaScript = "application/javascript";
    public const string ApplicationJson = "application/json";
    public const string ApplicationOctetStream = "application/octet-stream";
    public const string ApplicationPdf = "application/pdf";
    public const string ApplicationProblemJson = "application/problem+json";
    public const string ApplicationVndMsFontObject = "application/vnd.ms-fontobject";
    public const string ApplicationWasm = "application/wasm";
    public const string ApplicationXml = "application/xml";
    public const string ApplicationZip = "application/zip";
    public const string FontOtf = "font/otf";
    public const string FontTtf = "font/ttf";
    public const string FontWoff = "font/woff";
    public const string FontWoff2 = "font/woff2";
    public const string ImageGif = "image/gif";
    public const string ImageJpeg = "image/jpeg";
    public const string ImagePng = "image/png";
    public const string ImageSvgXml = "image/svg+xml";
    public const string ImageWebp = "image/webp";
    public const string ImageXIcon = "image/x-icon";
    public const string TextCss = "text/css";
    public const string TextEventStream = "text/event-stream";
    public const string TextHtml = "text/html";
    public const string TextJavaScript = "text/javascript";
    public const string TextPlain = "text/plain";
}

public static class HttpVersions
{
    public const string Http10 = "HTTP/1.0";
    public const string Http11 = "HTTP/1.1";
}

public static class HttpMethods
{
    public const string Connect = "CONNECT";
    public const string Delete = "DELETE";
    public const string Get = "GET";
    public const string Head = "HEAD";
    public const string Options = "OPTIONS";
    public const string Patch = "PATCH";
    public const string Post = "POST";
    public const string Put = "PUT";
    public const string Trace = "TRACE";

    public const string AllowAll = "GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS";
}

public static class HeaderValues
{
    public const string Brotli = "br";
    public const string Bytes = "bytes";
    public const string Chunked = "chunked";
    public const string Close = "close";
    public const string Gzip = "gzip";
    public const string KeepAlive = "keep-alive";
    public const string NoCache = "no-cache";
    public const string NoStore = "no-store";
    public const string Websocket = "websocket";
}
