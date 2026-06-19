using System.Security.Cryptography;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core;

public sealed partial class EffinitiveServer
{
    /// <summary>
    /// Result of request validation — determines connection flow.
    /// </summary>
    internal enum ValidationAction
    {
        Continue,
        CloseConnection,
        SendAndContinue
    }

    internal readonly struct ValidationResult
    {
        public ValidationAction Action { get; init; }
        public HttpResponse? Response { get; init; }

        public static ValidationResult Ok() => new() { Action = ValidationAction.Continue };
        public static ValidationResult Close(HttpResponse? response = null) => new() { Action = ValidationAction.CloseConnection, Response = response };
        public static ValidationResult Respond(HttpResponse response) => new() { Action = ValidationAction.SendAndContinue, Response = response };
    }

    /// <summary>
    /// Pre-routing security and compliance checks (RFC 9110, RFC 9112).
    /// Returns a ValidationResult indicating whether to continue, close, or send-and-continue.
    /// </summary>
    internal ValidationResult ValidateRequest(HttpRequest request)
    {
        // RFC 9112 §2.3: Drop connection for unsupported HTTP major/minor versions
        if (request.HttpVersion != HttpVersions.Http11 && request.HttpVersion != HttpVersions.Http10)
            return ValidationResult.Close();

        // Reject HTTP/1.0 without Host header (security: prevent host confusion)
        if (request.HttpVersion == HttpVersions.Http10 && !request.Headers.ContainsKey(HeaderNames.Host))
        {
            return ValidationResult.Close(new HttpResponse
            {
                StatusCode = 400, KeepAlive = false,
                Body = System.Text.Encoding.UTF8.GetBytes("Missing Host header"),
                ContentType = MediaTypes.TextPlain
            });
        }

        // Reject absolute-form URI with Host header mismatch (RFC 9112 §3.2.2)
        if (request.Items != null &&
            request.Items.TryGetValue("AbsoluteFormHost", out var absHostObj) &&
            absHostObj is string absHost &&
            request.Headers.TryGetValue(HeaderNames.Host, out var hostVal))
        {
            static string StripPort(string h) { var i = h.LastIndexOf(':'); return i > 0 ? h[..i] : h; }
            if (!StripPort(absHost).Equals(StripPort(hostVal), StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("Absolute-form URI host does not match Host header"),
                    ContentType = MediaTypes.TextPlain
                });
            }
        }

        // Reject Range header with excessive ranges (CVE-2011-3192 class DoS)
        if (request.Headers.TryGetValue(HeaderNames.Range, out var rangeVal) &&
            rangeVal.Split(',').Length > 100)
        {
            return ValidationResult.Close();
        }

        // Reject GET/HEAD/OPTIONS with Content-Length body (smuggling vector)
        if (request.ContentLength > 0)
        {
            if (request.Method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("GET with request body not accepted"),
                    ContentType = MediaTypes.TextPlain
                });
            }
            if (request.Method.Equals(HttpMethods.Head, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("HEAD with request body not accepted"),
                    ContentType = MediaTypes.TextPlain
                });
            }
            if (request.Method.Equals(HttpMethods.Options, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("OPTIONS with request body not accepted"),
                    ContentType = MediaTypes.TextPlain
                });
            }
        }

        // Close connection after POST with CL:0 (prevent body-poison attacks)
        if (request.Method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase) && request.ContentLength == 0)
        {
            request.KeepAlive = false;
        }

        // HTTP/1.0 defaults to Connection: close unless explicit keep-alive
        if (request.HttpVersion == HttpVersions.Http10)
        {
            if (!request.Headers.TryGetValue(HeaderNames.Connection, out var connHeader) ||
                !connHeader.Equals(HeaderValues.KeepAlive, StringComparison.OrdinalIgnoreCase))
            {
                request.KeepAlive = false;
            }
        }

        // RFC 9110 §10.1.1: Reject unknown Expect header values with 417
        if (request.Headers.TryGetValue(HeaderNames.Expect, out var expectValue))
        {
            if (!expectValue.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                var expectResponse = new HttpResponse
                {
                    StatusCode = 417,
                    KeepAlive = request.KeepAlive,
                    Body = System.Text.Encoding.UTF8.GetBytes("Expectation Failed"),
                    ContentType = MediaTypes.TextPlain
                };
                return request.KeepAlive
                    ? ValidationResult.Respond(expectResponse)
                    : ValidationResult.Close(expectResponse);
            }
            // Expect: 100-continue — body may already be sent by clients like curl.
            // Just proceed normally; the body has already been read by the parser.
        }

        // Content negotiation: reject unsupported Accept types (RFC 9110 §12.5.1)
        if (request.Headers.TryGetValue(HeaderNames.Accept, out var acceptVal) &&
            !acceptVal.Contains("*/*") &&
            !acceptVal.Contains("text/") &&
            !acceptVal.Contains(MediaTypes.ApplicationJson))
        {
            var notAcceptableResponse = new HttpResponse
            {
                StatusCode = 406, KeepAlive = request.KeepAlive,
                Body = System.Text.Encoding.UTF8.GetBytes("Not Acceptable"),
                ContentType = MediaTypes.TextPlain
            };
            return request.KeepAlive
                ? ValidationResult.Respond(notAcceptableResponse)
                : ValidationResult.Close(notAcceptableResponse);
        }

        return ValidationResult.Ok();
    }

    /// <summary>
    /// Apply ETag/conditional response headers for GET/HEAD 2xx responses (RFC 9110 §13.1).
    /// </summary>
    internal void ApplyConditionalHeaders(HttpRequest request, HttpResponse response, bool isHead)
    {
        if ((!request.Method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase) && !isHead)
            || response.StatusCode < 200 || response.StatusCode >= 300
            || response.StatusCode == 204
            || response.IsStreaming)
            return;

        // Generate ETag from response body if not already set
        if (!response.Headers.ContainsKey(HeaderNames.ETag))
        {
            var hash = SHA256.HashData(response.Body ?? Array.Empty<byte>());
            response.Headers[HeaderNames.ETag] = $"\"{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}\"";
        }

        // Set Last-Modified if not already set
        if (!response.Headers.ContainsKey(HeaderNames.LastModified))
        {
            response.Headers[HeaderNames.LastModified] = _serverStartTimeRfc;
        }

        // Check If-None-Match (takes precedence per RFC 9110 §13.1.2)
        if (request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch))
        {
            if (WeakETagMatch(ifNoneMatch, response.Headers[HeaderNames.ETag]))
            {
                var etag = response.Headers[HeaderNames.ETag];
                response.StatusCode = 304;
                response.Body = null;
                response.Headers[HeaderNames.ETag] = etag;
            }
        }
        // If-Modified-Since only when If-None-Match is absent (RFC 9110 §13.1.3)
        else if (request.Headers.TryGetValue(HeaderNames.IfModifiedSince, out var ifModifiedSince))
        {
            var formats = new[] { "R", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM  d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" };
            if (DateTime.TryParseExact(ifModifiedSince.Trim(), formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var sinceDate)
                && sinceDate <= DateTime.UtcNow  // RFC 9110 §13.1.3: ignore if in the future
                && _serverStartTime <= sinceDate)
            {
                var etag = response.Headers.TryGetValue(HeaderNames.ETag, out var e) ? e : null;
                response.StatusCode = 304;
                response.Body = null;
                if (etag != null) response.Headers[HeaderNames.ETag] = etag;
            }
        }
    }
}
