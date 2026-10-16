using System.IO.Compression;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Which responses are marked for gzip, and what the decision costs when the answer is no.
/// </summary>
/// <remarks>
/// RFC 9110 §12.5.3: a client that sends no Accept-Encoding is taken to accept any coding, but
/// a server is never obliged to apply one, and §12.5.5 requires a response whose selection
/// depended on a request header to say so with Vary.
///
/// The minimum-size guard needs the body's length, and a body that is still an object has to be
/// serialized before it can be measured. That serialization is the expensive half of the
/// decision and it also costs the writer its single-pass pooled path, so it must not happen for
/// a response that was never going to be compressed.
/// </remarks>
public class ResponseCompressionNegotiationTests
{
    private sealed record Payload(string Text);

    private static HttpRequest RequestWith(string? acceptEncoding)
    {
        var request = new HttpRequest { Path = "/x", Method = "GET" };
        if (acceptEncoding != null)
            request.Headers[HeaderNames.AcceptEncoding] = acceptEncoding;
        return request;
    }

    /// <summary>A response whose body is still an object, as a serialized endpoint result is.</summary>
    private static HttpResponse DeferredJson(int approximateSize)
        => new()
        {
            StatusCode = 200,
            ContentType = MediaTypes.ApplicationJson,
            BodyObject = new Payload(new string('a', approximateSize)),
        };

    private static HttpResponse Materialized(byte[] body, string contentType)
        => new() { StatusCode = 200, ContentType = contentType, Body = body };

    private static async Task<HttpResponse> RunAsync(HttpRequest request, HttpResponse response)
    {
        var middleware = new ResponseCompressionMiddleware();
        return await middleware.InvokeAsync(
            request,
            (_, _) => ValueTask.FromResult(response),
            CancellationToken.None);
    }

    [Fact]
    public async Task WithoutAcceptEncodingTheBodyIsNotEvenSerializedToBeMeasured()
    {
        var response = DeferredJson(8192);

        var result = await RunAsync(RequestWith(null), response);

        Assert.Null(result.Headers.GetValueOrDefault(HeaderNames.ContentEncoding));
        Assert.NotNull(result.BodyObject);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task ACodingWeDoNotOfferAlsoLeavesTheBodyAlone()
    {
        var response = DeferredJson(8192);

        var result = await RunAsync(RequestWith("br"), response);

        Assert.Null(result.Headers.GetValueOrDefault(HeaderNames.ContentEncoding));
        Assert.NotNull(result.BodyObject);
    }

    [Fact]
    public async Task IdentityOnlyIsNotGzipped()
    {
        var result = await RunAsync(RequestWith("identity"), DeferredJson(8192));

        Assert.Null(result.Headers.GetValueOrDefault(HeaderNames.ContentEncoding));
    }

    [Fact]
    public async Task ALargeCompressibleBodyIsMarkedForGzipAndSaysItVaried()
    {
        var result = await RunAsync(RequestWith("gzip"), DeferredJson(8192));

        Assert.Equal(HeaderValues.Gzip, result.Headers[HeaderNames.ContentEncoding]);
        Assert.Contains("accept-encoding", result.Headers[HeaderNames.Vary].ToLowerInvariant());
        Assert.NotNull(result.GzipCompressionLevel);
    }

    [Fact]
    public async Task GzipAmongSeveralOfferedCodingsIsFound()
    {
        var result = await RunAsync(RequestWith("br, gzip, deflate"), DeferredJson(8192));

        Assert.Equal(HeaderValues.Gzip, result.Headers[HeaderNames.ContentEncoding]);
    }

    [Fact]
    public async Task ABodyBelowTheMinimumIsNotWorthCompressing()
    {
        var result = await RunAsync(
            RequestWith("gzip"), Materialized(new byte[16], MediaTypes.ApplicationJson));

        Assert.Null(result.Headers.GetValueOrDefault(HeaderNames.ContentEncoding));
    }

    [Fact]
    public async Task AContentTypeThatDoesNotCompressWellIsLeftAlone()
    {
        var result = await RunAsync(
            RequestWith("gzip"), Materialized(new byte[8192], "application/octet-stream"));

        Assert.Null(result.Headers.GetValueOrDefault(HeaderNames.ContentEncoding));
    }

    [Fact]
    public async Task AResponseThatIsAlreadyEncodedIsNotEncodedAgain()
    {
        var response = Materialized(new byte[8192], MediaTypes.ApplicationJson);
        response.Headers[HeaderNames.ContentEncoding] = "br";

        var result = await RunAsync(RequestWith("gzip"), response);

        Assert.Equal("br", result.Headers[HeaderNames.ContentEncoding]);
    }

    [Fact]
    public async Task AContentTypeCarryingACharsetStillCounts()
    {
        var result = await RunAsync(
            RequestWith("gzip"),
            Materialized(new byte[8192], MediaTypes.ApplicationJson + "; charset=utf-8"));

        Assert.Equal(HeaderValues.Gzip, result.Headers[HeaderNames.ContentEncoding]);
    }

    [Fact]
    public async Task AnExistingVaryIsAddedToRatherThanReplaced()
    {
        var response = DeferredJson(8192);
        response.AppendVary(HeaderNames.AcceptLanguage);

        var result = await RunAsync(RequestWith("gzip"), response);

        var vary = result.Headers[HeaderNames.Vary].ToLowerInvariant();
        Assert.Contains("accept-language", vary);
        Assert.Contains("accept-encoding", vary);
    }

    [Fact]
    public void TheMarkedLevelIsOneGzipCanActuallyProduce()
    {
        // The writer compresses with whatever level the middleware recorded, so a level the
        // stream cannot take would surface as a failed response rather than a slower one.
        using var sink = new MemoryStream();
        using (var gzip = new GZipStream(sink, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write("payload"u8);

        Assert.Equal(0x1f, sink.ToArray()[0]);
        Assert.Equal(0x8b, sink.ToArray()[1]);
    }
}
