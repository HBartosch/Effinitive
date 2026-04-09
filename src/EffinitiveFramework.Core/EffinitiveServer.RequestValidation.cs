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
        if (request.HttpVersion != "HTTP/1.1" && request.HttpVersion != "HTTP/1.0")
            return ValidationResult.Close();

        // Reject HTTP/1.0 without Host header (security: prevent host confusion)
        if (request.HttpVersion == "HTTP/1.0" && !request.Headers.ContainsKey("Host"))
        {
            return ValidationResult.Close(new HttpResponse
            {
                StatusCode = 400, KeepAlive = false,
                Body = System.Text.Encoding.UTF8.GetBytes("Missing Host header"),
                ContentType = "text/plain"
            });
        }

        // Reject absolute-form URI with Host header mismatch (RFC 9112 §3.2.2)
        if (request.Items != null &&
            request.Items.TryGetValue("AbsoluteFormHost", out var absHostObj) &&
            absHostObj is string absHost &&
            request.Headers.TryGetValue("Host", out var hostVal))
        {
            static string StripPort(string h) { var i = h.LastIndexOf(':'); return i > 0 ? h[..i] : h; }
            if (!StripPort(absHost).Equals(StripPort(hostVal), StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("Absolute-form URI host does not match Host header"),
                    ContentType = "text/plain"
                });
            }
        }

        // Reject Range header with excessive ranges (CVE-2011-3192 class DoS)
        if (request.Headers.TryGetValue("Range", out var rangeVal) &&
            rangeVal.Split(',').Length > 100)
        {
            return ValidationResult.Close();
        }

        // Reject GET/HEAD/OPTIONS with Content-Length body (smuggling vector)
        if (request.ContentLength > 0)
        {
            if (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("GET with request body not accepted"),
                    ContentType = "text/plain"
                });
            }
            if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("HEAD with request body not accepted"),
                    ContentType = "text/plain"
                });
            }
            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("OPTIONS with request body not accepted"),
                    ContentType = "text/plain"
                });
            }
        }

        // Close connection after POST with CL:0 (prevent body-poison attacks)
        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && request.ContentLength == 0)
        {
            request.KeepAlive = false;
        }

        // HTTP/1.0 defaults to Connection: close unless explicit keep-alive
        if (request.HttpVersion == "HTTP/1.0")
        {
            if (!request.Headers.TryGetValue("Connection", out var connHeader) ||
                !connHeader.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                request.KeepAlive = false;
            }
        }

        // RFC 9110 §10.1.1: Reject unknown Expect header values with 417
        if (request.Headers.TryGetValue("Expect", out var expectValue))
        {
            if (!expectValue.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                var expectResponse = new HttpResponse
                {
                    StatusCode = 417,
                    KeepAlive = request.KeepAlive,
                    Body = System.Text.Encoding.UTF8.GetBytes("Expectation Failed"),
                    ContentType = "text/plain"
                };
                return request.KeepAlive
                    ? ValidationResult.Respond(expectResponse)
                    : ValidationResult.Close(expectResponse);
            }
            // Expect: 100-continue with body already sent — reject as smuggling vector
            if (request.ContentLength > 0 && request.Body.Length > 0)
            {
                return ValidationResult.Close(new HttpResponse
                {
                    StatusCode = 400, KeepAlive = false,
                    Body = System.Text.Encoding.UTF8.GetBytes("Expect: 100-continue with body already sent"),
                    ContentType = "text/plain"
                });
            }
        }

        // Content negotiation: reject unsupported Accept types (RFC 9110 §12.5.1)
        if (request.Headers.TryGetValue("Accept", out var acceptVal) &&
            !acceptVal.Contains("*/*") &&
            !acceptVal.Contains("text/") &&
            !acceptVal.Contains("application/json"))
        {
            var notAcceptableResponse = new HttpResponse
            {
                StatusCode = 406, KeepAlive = request.KeepAlive,
                Body = System.Text.Encoding.UTF8.GetBytes("Not Acceptable"),
                ContentType = "text/plain"
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
        if ((!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !isHead)
            || response.StatusCode < 200 || response.StatusCode >= 300
            || response.StatusCode == 204)
            return;

        // Generate ETag from response body if not already set
        if (!response.Headers.ContainsKey("ETag"))
        {
            var hash = SHA256.HashData(response.Body ?? Array.Empty<byte>());
            response.Headers["ETag"] = $"\"{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}\"";
        }

        // Set Last-Modified if not already set
        if (!response.Headers.ContainsKey("Last-Modified"))
        {
            response.Headers["Last-Modified"] = _serverStartTimeRfc;
        }

        // Check If-None-Match (takes precedence per RFC 9110 §13.1.2)
        if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch))
        {
            if (WeakETagMatch(ifNoneMatch, response.Headers["ETag"]))
            {
                var etag = response.Headers["ETag"];
                response.StatusCode = 304;
                response.Body = null;
                response.Headers["ETag"] = etag;
            }
        }
        // If-Modified-Since only when If-None-Match is absent (RFC 9110 §13.1.3)
        else if (request.Headers.TryGetValue("If-Modified-Since", out var ifModifiedSince))
        {
            var formats = new[] { "R", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM  d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" };
            if (DateTime.TryParseExact(ifModifiedSince.Trim(), formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var sinceDate)
                && sinceDate <= DateTime.UtcNow  // RFC 9110 §13.1.3: ignore if in the future
                && _serverStartTime <= sinceDate)
            {
                var etag = response.Headers.TryGetValue("ETag", out var e) ? e : null;
                response.StatusCode = 304;
                response.Body = null;
                if (etag != null) response.Headers["ETag"] = etag;
            }
        }
    }
}
