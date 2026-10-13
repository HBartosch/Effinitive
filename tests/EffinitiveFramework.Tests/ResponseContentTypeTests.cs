using System.Net;
using System.Net.Sockets;
using System.Text;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// An endpoint that names its own content type and returns the text of it must get that text on
/// the wire, byte for byte.
/// </summary>
/// <remarks>
/// RFC 9110 §8.3 makes Content-Type the sender's statement of what the content is. Serialising a
/// string through the JSON writer while labelling the result text/html breaks that statement: the
/// body arrives as a JSON string literal, with the markup escaped to < and the whole thing
/// wrapped in quotes, under a header promising HTML. A browser renders the escapes as text and an
/// HTML parser finds no document.
///
/// The branch that decided this tested the content type for ordinal equality with the literal
/// "text/plain", so every other textual type fell through to the serialiser. That is the whole
/// bug: text/html, text/csv, application/xml and even "text/plain; charset=utf-8" were all
/// treated as JSON. A string under a JSON type is still serialised, because there the quoting is
/// the correct representation.
/// </remarks>
public class ResponseContentTypeTests : IAsyncLifetime
{
    private const string Markup = "<!DOCTYPE html><html><body><p>a &amp; b</p></body></html>";

    public sealed class HtmlEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/html";
        protected override string ContentType => "text/html; charset=utf-8";

        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Markup);
    }

    /// <summary>The same thing from the async base, which is the shape a database-backed page takes.</summary>
    public sealed class AsyncHtmlEndpoint : NoRequestAsyncEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/html-async";
        protected override string ContentType => "text/html";

        public override Task<string> HandleAsync(CancellationToken ct = default)
            => Task.FromResult(Markup);
    }

    /// <summary>A charset parameter does not stop a type being text/plain (RFC 9110 §8.3.1).</summary>
    public sealed class PlainWithCharsetEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/plain";
        protected override string ContentType => "text/plain; charset=utf-8";

        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult("hello");
    }

    /// <summary>Under a JSON type the quoting is the representation, so it must stay.</summary>
    public sealed class JsonStringEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/json";

        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult("hello");
    }

    private EffinitiveServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Type[] endpoints =
            [typeof(HtmlEndpoint), typeof(AsyncHtmlEndpoint), typeof(PlainWithCharsetEndpoint), typeof(JsonStringEndpoint)];

        var router = new Router();
        router.AddEndpointType("GET", "/html", typeof(HtmlEndpoint), EndpointInvoker.Build(typeof(HtmlEndpoint)));
        router.AddEndpointType("GET", "/html-async", typeof(AsyncHtmlEndpoint), EndpointInvoker.Build(typeof(AsyncHtmlEndpoint)));
        router.AddEndpointType("GET", "/plain", typeof(PlainWithCharsetEndpoint), EndpointInvoker.Build(typeof(PlainWithCharsetEndpoint)));
        router.AddEndpointType("GET", "/json", typeof(JsonStringEndpoint), EndpointInvoker.Build(typeof(JsonStringEndpoint)));
        router.Freeze();

        var services = new ServiceCollection();
        foreach (var endpoint in endpoints)
            services.AddTransient(endpoint, endpoint);
        var provider = services.BuildServiceProvider();

        _cts = new CancellationTokenSource();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            _port = FreePort();
            _server = new EffinitiveServer(
                new ServerOptions { HttpPort = _port, EnableDebugLogging = false }, router, provider);

            _ = _server.StartAsync(_cts.Token);

            if (await Listening(_port))
                return;
        }

        throw new InvalidOperationException("test server never started listening");
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HtmlIsSentAsHtmlRatherThanAsAJsonStringLiteral()
    {
        var body = await GetAsync("/html");

        Assert.Equal(Markup, body);
    }

    [Fact]
    public async Task TheAsyncBaseSendsHtmlTheSameWay()
    {
        var body = await GetAsync("/html-async");

        Assert.Equal(Markup, body);
    }

    [Fact]
    public async Task ACharsetParameterDoesNotTurnTextIntoJson()
    {
        var body = await GetAsync("/plain");

        Assert.Equal("hello", body);
    }

    [Fact]
    public async Task AStringUnderAJsonTypeIsStillSerialised()
    {
        var body = await GetAsync("/json");

        Assert.Equal("\"hello\"", body);
    }

    /// <summary>Sends one request and returns exactly the content the framing describes.</summary>
    private async Task<string> GetAsync(string path)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();

        var request = $"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        var buffer = new byte[64 * 1024];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled));
            if (read == 0) break;
            filled += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, filled);
        int separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator > 0, $"no header terminator in the response: {text}");

        return text[(separator + 4)..];
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<bool> Listening(int port)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        return false;
    }
}
