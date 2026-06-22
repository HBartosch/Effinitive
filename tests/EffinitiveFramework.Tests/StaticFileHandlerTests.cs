using System.Text;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.StaticFiles;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for <see cref="StaticFileHandler"/>: per-request disk serving, conditional requests
/// (RFC 9110 §13), range requests (§14), Accept-Encoding negotiation, and path-traversal safety.
/// </summary>
public sealed class StaticFileHandlerTests : IDisposable
{
    private readonly string _root;
    private readonly StaticFileHandler _handler;

    private const string Prefix = "/static";
    private static readonly byte[] CssBytes = Encoding.UTF8.GetBytes("body { color: red; }\n");

    public StaticFileHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "effinitive-staticfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "css"));
        File.WriteAllBytes(Path.Combine(_root, "css", "site.css"), CssBytes);
        File.WriteAllText(Path.Combine(_root, "index.html"), "<h1>home</h1>");
        // Pre-compressed sidecars (content is irrelevant for these tests — only that they're served).
        File.WriteAllBytes(Path.Combine(_root, "css", "site.css.br"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_root, "css", "site.css.gz"), new byte[] { 9, 8, 7 });

        _handler = new StaticFileHandler(new StaticFileOptions
        {
            RootPath = _root,
            RequestPath = Prefix,
            CacheControl = "public, max-age=3600"
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static HttpRequest Request(string method, string path)
    {
        var req = new HttpRequest { Method = method, Path = path, HttpVersion = "HTTP/1.1" };
        req.Headers["Host"] = "localhost";
        return req;
    }

    private static byte[] ReadBody(HttpResponse response)
    {
        Assert.NotNull(response.BodyStream);
        var buffer = new byte[response.BodyStreamLength];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = response.BodyStream!.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }
        return buffer;
    }

    [Fact]
    public void Serves_ExistingFile_With200AndValidators()
    {
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(Request("GET", "/static/css/site.css"), response));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(MediaTypes.TextCss, response.ContentType);
        Assert.Equal("public, max-age=3600", response.Headers[HeaderNames.CacheControl]);
        Assert.StartsWith("\"", response.Headers[HeaderNames.ETag]);
        Assert.True(response.Headers.ContainsKey(HeaderNames.LastModified));
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.Equal(CssBytes, ReadBody(response));
    }

    [Fact]
    public void UnknownPath_ReturnsFalse()
    {
        var response = new HttpResponse();
        Assert.False(_handler.TryServe(Request("GET", "/static/css/missing.css"), response));
    }

    [Fact]
    public void NonPrefixPath_ReturnsFalse()
    {
        var response = new HttpResponse();
        Assert.False(_handler.TryServe(Request("GET", "/api/users"), response));
    }

    [Theory]
    [InlineData("/static/../secret.txt")]
    [InlineData("/static/..%2f..%2fsecret.txt")]
    [InlineData("/static/css/..%2f..%2f..%2fwindows%2fwin.ini")]
    public void Traversal_IsRejected(string path)
    {
        // A real file just outside the root that an escape would expose.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "secret.txt"), "secret");
        var response = new HttpResponse();
        Assert.False(_handler.TryServe(Request("GET", path), response));
    }

    [Fact]
    public void DirectoryRequest_ServesDefaultFile()
    {
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(Request("GET", "/static/"), response));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(MediaTypes.TextHtml, response.ContentType);
    }

    [Fact]
    public void IfNoneMatch_MatchingETag_Returns304WithNoBody()
    {
        var first = new HttpResponse();
        _handler.TryServe(Request("GET", "/static/css/site.css"), first);
        var etag = first.Headers[HeaderNames.ETag];

        var req = Request("GET", "/static/css/site.css");
        req.Headers[HeaderNames.IfNoneMatch] = etag;
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(req, response));

        Assert.Equal(304, response.StatusCode);
        Assert.Null(response.BodyStream);
        Assert.Null(response.Body);
        Assert.Equal(etag, response.Headers[HeaderNames.ETag]);
    }

    [Fact]
    public void IfModifiedSince_PastDate_Returns200_FutureOrEqual_Returns304()
    {
        var probe = new HttpResponse();
        _handler.TryServe(Request("GET", "/static/css/site.css"), probe);
        var lastModified = probe.Headers[HeaderNames.LastModified];

        // Client already has the current version.
        var unchanged = Request("GET", "/static/css/site.css");
        unchanged.Headers[HeaderNames.IfModifiedSince] = lastModified;
        var r1 = new HttpResponse();
        _handler.TryServe(unchanged, r1);
        Assert.Equal(304, r1.StatusCode);

        // Client's copy is from before the file existed.
        var stale = Request("GET", "/static/css/site.css");
        stale.Headers[HeaderNames.IfModifiedSince] = "Tue, 01 Jan 1980 00:00:00 GMT";
        var r2 = new HttpResponse();
        _handler.TryServe(stale, r2);
        Assert.Equal(200, r2.StatusCode);
    }

    [Fact]
    public void RangeRequest_Returns206WithContentRangeAndPartialBody()
    {
        var req = Request("GET", "/static/css/site.css");
        req.Headers[HeaderNames.Range] = "bytes=0-3";
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(req, response));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal($"bytes 0-3/{CssBytes.Length}", response.Headers["Content-Range"]);
        Assert.Equal(4, response.BodyStreamLength);
        Assert.Equal(CssBytes[..4], ReadBody(response));
    }

    [Fact]
    public void SuffixRange_ReturnsLastBytes()
    {
        var req = Request("GET", "/static/css/site.css");
        req.Headers[HeaderNames.Range] = "bytes=-5";
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(req, response));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal(5, response.BodyStreamLength);
        Assert.Equal(CssBytes[^5..], ReadBody(response));
    }

    [Fact]
    public void UnsatisfiableRange_Returns416()
    {
        var req = Request("GET", "/static/css/site.css");
        req.Headers[HeaderNames.Range] = $"bytes={CssBytes.Length + 100}-{CssBytes.Length + 200}";
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(req, response));

        Assert.Equal(416, response.StatusCode);
        Assert.Equal($"bytes */{CssBytes.Length}", response.Headers["Content-Range"]);
    }

    [Fact]
    public void AcceptEncoding_PrefersBrotliSidecar()
    {
        var req = Request("GET", "/static/css/site.css");
        req.Headers[HeaderNames.AcceptEncoding] = "br;q=1.0, gzip;q=0.8";
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(req, response));

        Assert.Equal(HeaderValues.Brotli, response.Headers[HeaderNames.ContentEncoding]);
        Assert.Equal(HeaderNames.AcceptEncoding, response.Headers[HeaderNames.Vary]);
        // The brotli sidecar's content, not the identity file.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ReadBody(response));
    }

    [Fact]
    public void AcceptEncoding_QualityZero_RejectsThatCoding()
    {
        var req = Request("GET", "/static/css/site.css");
        req.Headers[HeaderNames.AcceptEncoding] = "br;q=0, gzip;q=1.0";
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(req, response));

        Assert.Equal(HeaderValues.Gzip, response.Headers[HeaderNames.ContentEncoding]);
    }

    [Fact]
    public void Head_SetsContentLength_WithoutBody()
    {
        var response = new HttpResponse();
        Assert.True(_handler.TryServe(Request("HEAD", "/static/css/site.css"), response));

        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.BodyStream);
        Assert.Null(response.Body);
        Assert.Equal(CssBytes.Length.ToString(), response.Headers[HeaderNames.ContentLength]);
    }
}
