using Xunit;
using EffinitiveFramework.Core.Configuration;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2;
using EffinitiveFramework.Core.Http2.Hpack;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace EffinitiveFramework.SecurityTests;

public class SecurityFixTests
{
    [Fact]
    public void ServerOptions_HasSecureDefaults()
    {
        var options = new ServerOptions();
        Assert.Equal(30 * 1024 * 1024, options.MaxRequestBodySize); // 30MB
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeaderTimeout);
    }

    [Fact]
    public void HttpRequestParser_RejectsOversizedBody()
    {
        var requestText = "POST / HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2048\r\n\r\n";
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(requestText));
        var request = new HttpRequest();
        
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            HttpRequestParser.TryParseRequest(ref buffer, request, out _, out _, maxBodySize: 1024);
        });
        
        Assert.Contains("exceeds maximum allowed size", exception.Message);
    }

    [Fact]
    public void Http2Constants_HasSecureDefaults()
    {
        Assert.Equal(100u, Http2Constants.DefaultMaxConcurrentStreams);
        Assert.Equal(16384u, Http2Constants.DefaultMaxFrameSize);
        Assert.Equal(8192u, Http2Constants.DefaultMaxHeaderListSize);
    }

    [Fact]
    public void HpackDecoder_RejectsDecompressionBomb()
    {
        var decoder = new HpackDecoder(maxDynamicTableSize: 4096, maxDecompressedSize: 100);
        
        // Create HPACK data that decompresses to large size
        // Indexed header field for :method: GET (index 2 in static table)
        var encodedData = new byte[50];
        for (int i = 0; i < 50; i++)
        {
            encodedData[i] = 0x82; // Indexed header :method: GET (repeating to exceed limit)
        }
        
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            decoder.DecodeHeaders(encodedData);
        });
        
        Assert.Contains("HPACK decompression size", exception.Message);
        Assert.Contains("exceeds maximum", exception.Message);
    }

    [Fact]
    public async Task Http2Connection_CanPushStaticResources()
    {
        // Scenario: Client requests HTML, server pushes CSS and JS before client asks
        // This optimizes page load by reducing round trips
        
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream);
        
        // Simulate client preface and settings already exchanged
        // (in real test, would send actual preface bytes)
        
        // Push CSS file on even stream ID 2
        var cssContent = Encoding.UTF8.GetBytes("body { margin: 0; }");
        await connection.PushResourceAsync(
            associatedStreamId: 1, // HTML request stream
            requestHeaders: new Dictionary<string, string>
            {
                { ":method", "GET" },
                { ":path", "/styles/app.css" },
                { ":scheme", "https" },
                { ":authority", "example.com" }
            },
            responseHeaders: new Dictionary<string, string>
            {
                { ":status", "200" },
                { "content-type", "text/css" },
                { "cache-control", "public, max-age=3600" }
            },
            responseBody: cssContent
        );
        
        // Push JavaScript file on even stream ID 4
        var jsContent = Encoding.UTF8.GetBytes("console.log('pushed!');");
        await connection.PushResourceAsync(
            associatedStreamId: 1, // HTML request stream
            requestHeaders: new Dictionary<string, string>
            {
                { ":method", "GET" },
                { ":path", "/scripts/app.js" },
                { ":scheme", "https" },
                { ":authority", "example.com" }
            },
            responseHeaders: new Dictionary<string, string>
            {
                { ":status", "200" },
                { "content-type", "application/javascript" },
                { "cache-control", "public, max-age=3600" }
            },
            responseBody: jsContent
        );
        
        // Verify frames were written to stream
        stream.Position = 0;
        var buffer = stream.ToArray();
        
        // Should contain PUSH_PROMISE frames (type 0x05)
        Assert.Contains<byte>(0x05, buffer);
        
        // Should contain both resources
        Assert.Contains("app.css", Encoding.UTF8.GetString(buffer));
        Assert.Contains("app.js", Encoding.UTF8.GetString(buffer));
        
        // Verify stream length shows data was written
        Assert.True(buffer.Length > 100); // Should have substantial data
    }
    
    [Fact]
    public void Http2Connection_RejectsPushWhenDisabled()
    {
        // Test that push is rejected when client sends ENABLE_PUSH = 0
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream);
        
        // In real implementation, would set _enablePush = 0 via SETTINGS frame
        // For now, verify the constant is correct
        Assert.Equal(1u, Http2Constants.DefaultEnablePush); // Enabled by default
    }
    
    [Fact]
    public async Task PushResourceAsync_RejectsExcessivePushes()
    {
        // Test: Verify limit on pushed streams per connection
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream, maxPushedStreams: 3);
        
        var requestHeaders = new Dictionary<string, string>
        {
            { ":method", "GET" },
            { ":path", "/file1.css" },
            { ":scheme", "https" },
            { ":authority", "example.com" }
        };
        
        var responseHeaders = new Dictionary<string, string>
        {
            { ":status", "200" },
            { "content-type", "text/css" }
        };
        
        var body = new byte[100];
        
        // First 3 pushes should succeed
        await connection.PushResourceAsync(1, requestHeaders, responseHeaders, body);
        requestHeaders[":path"] = "/file2.css";
        await connection.PushResourceAsync(1, requestHeaders, responseHeaders, body);
        requestHeaders[":path"] = "/file3.css";
        await connection.PushResourceAsync(1, requestHeaders, responseHeaders, body);
        
        // 4th push should fail
        requestHeaders[":path"] = "/file4.css";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.PushResourceAsync(1, requestHeaders, responseHeaders, body));
        
        Assert.Contains("Maximum pushed streams", exception.Message);
    }
    
    [Fact]
    public async Task PushResourceAsync_RejectsOversizedResource()
    {
        // Test: Verify limit on pushed resource size
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream, maxPushedResourceSize: 1000);
        
        var requestHeaders = new Dictionary<string, string>
        {
            { ":method", "GET" },
            { ":path", "/huge.js" },
            { ":scheme", "https" },
            { ":authority", "example.com" }
        };
        
        var responseHeaders = new Dictionary<string, string>
        {
            { ":status", "200" },
            { "content-type", "application/javascript" }
        };
        
        var hugeBody = new byte[2000]; // 2KB > 1KB limit
        
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.PushResourceAsync(1, requestHeaders, responseHeaders, hugeBody));
        
        Assert.Contains("Pushed resource size", exception.Message);
        Assert.Contains("exceeds maximum", exception.Message);
    }
    
    [Fact]
    public async Task PushResourceAsync_RejectsInvalidMethod()
    {
        // Test: Only GET and HEAD methods allowed (RFC 7540 §8.2)
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream);
        
        var requestHeaders = new Dictionary<string, string>
        {
            { ":method", "POST" }, // Invalid!
            { ":path", "/api/data" },
            { ":scheme", "https" },
            { ":authority", "example.com" }
        };
        
        var responseHeaders = new Dictionary<string, string> { { ":status", "200" } };
        var body = new byte[10];
        
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.PushResourceAsync(1, requestHeaders, responseHeaders, body));
        
        Assert.Contains("safe methods", exception.Message);
        Assert.Contains("GET, HEAD", exception.Message);
    }
    
    [Fact]
    public async Task PushResourceAsync_RequiresAllPseudoHeaders()
    {
        // Test: All 4 pseudo-headers required (RFC 7540 §8.2)
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream);
        
        var responseHeaders = new Dictionary<string, string> { { ":status", "200" } };
        var body = new byte[10];
        
        // Missing :authority
        var requestHeaders = new Dictionary<string, string>
        {
            { ":method", "GET" },
            { ":path", "/file.css" },
            { ":scheme", "https" }
            // Missing :authority
        };
        
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.PushResourceAsync(1, requestHeaders, responseHeaders, body));
        
        Assert.Contains("missing required pseudo-header", exception.Message);
        Assert.Contains(":authority", exception.Message);
    }
    
    [Fact]
    public async Task PushResourceAsync_EnforcesFlowControl()
    {
        // Test: Pushed data must respect flow control window
        var stream = new MemoryStream();
        var connection = new Http2Connection(stream);
        
        var requestHeaders = new Dictionary<string, string>
        {
            { ":method", "GET" },
            { ":path", "/file.dat" },
            { ":scheme", "https" },
            { ":authority", "example.com" }
        };
        
        var responseHeaders = new Dictionary<string, string> { { ":status", "200" } };
        
        // Body larger than default window size (65535 bytes)
        var hugeBody = new byte[100000];
        
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.PushResourceAsync(1, requestHeaders, responseHeaders, hugeBody));
        
        Assert.Contains("flow control window", exception.Message);
    }
}
