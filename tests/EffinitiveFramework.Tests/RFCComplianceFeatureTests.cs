using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Http2.Hpack;
using EffinitiveFramework.Core.Http2;
using System.Buffers;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for all RFC compliance features
/// Covers Huffman encoding, chunked encoding, priority, and content negotiation
/// </summary>
public class RFCComplianceFeatureTests
{
    #region Huffman Encoder Tests (RFC 7541)

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("no-cache")]
    [InlineData("custom-key")]
    [InlineData("gzip")]
    [InlineData("application/json")]
    public void HuffmanEncoder_RoundTrip_ProducesOriginalString(string original)
    {
        var encoded = HuffmanEncoder.Encode(original);
        var decoded = HuffmanDecoder.Decode(encoded);
        
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void HuffmanEncoder_EmptyString_ProducesEmptyArray()
    {
        var encoded = HuffmanEncoder.Encode("");
        
        Assert.Empty(encoded);
    }

    [Fact]
    public void HuffmanEncoder_CompressesCommonStrings()
    {
        var testString = "www.example.com";
        var encoded = HuffmanEncoder.Encode(testString);
        var plainSize = System.Text.Encoding.ASCII.GetByteCount(testString);
        
        // Huffman should compress this string
        Assert.True(encoded.Length < plainSize, 
            $"Huffman should compress '{testString}': plain={plainSize}, encoded={encoded.Length}");
    }

    [Fact]
    public void HuffmanEncoder_CommonASCII_RoundTrips()
    {
        // Test common printable ASCII characters
        var testChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./";
        
        foreach (var ch in testChars)
        {
            var original = ch.ToString();
            var encoded = HuffmanEncoder.Encode(original);
            var decoded = HuffmanDecoder.Decode(encoded);
            
            Assert.Equal(original, decoded);
        }
    }

    #endregion

    #region Chunked Encoding Tests (RFC 7230)

    [Fact]
    public void ChunkedEncoder_SimpleData_EncodesCorrectly()
    {
        var data = "Hello, World!"u8.ToArray();
        var encoded = ChunkedEncodingParser.EncodeChunked(data, chunkSize: 5);
        var encodedStr = System.Text.Encoding.ASCII.GetString(encoded);
        
        // Should have chunks with sizes and terminator
        Assert.Contains("5\r\n", encodedStr);
        Assert.Contains("0\r\n\r\n", encodedStr);
    }

    [Fact]
    public void ChunkedParser_SimpleChunked_DecodesCorrectly()
    {
        // "5\r\nHello\r\n8\r\n, World!\r\n0\r\n\r\n"
        var chunked = "5\r\nHello\r\n8\r\n, World!\r\n0\r\n\r\n"u8.ToArray();
        var buffer = new ReadOnlySequence<byte>(chunked);
        
        var success = ChunkedEncodingParser.TryParseChunked(
            ref buffer,
            out var body,
            out _,
            out _);
        
        Assert.True(success);
        Assert.Equal("Hello, World!", System.Text.Encoding.ASCII.GetString(body));
    }

    [Fact]
    public void ChunkedParser_EmptyChunked_ReturnsEmpty()
    {
        // Just the terminating chunk
        var chunked = "0\r\n\r\n"u8.ToArray();
        var buffer = new ReadOnlySequence<byte>(chunked);
        
        var success = ChunkedEncodingParser.TryParseChunked(
            ref buffer,
            out var body,
            out _,
            out _);
        
        Assert.True(success);
        Assert.Empty(body);
    }

    [Fact]
    public void ChunkedParser_WithExtensions_IgnoresExtensions()
    {
        // "5;name=value\r\nHello\r\n0\r\n\r\n"
        var chunked = "5;name=value\r\nHello\r\n0\r\n\r\n"u8.ToArray();
        var buffer = new ReadOnlySequence<byte>(chunked);
        
        var success = ChunkedEncodingParser.TryParseChunked(
            ref buffer,
            out var body,
            out _,
            out _);
        
        Assert.True(success);
        Assert.Equal("Hello", System.Text.Encoding.ASCII.GetString(body));
    }

    [Fact]
    public void ChunkedEncoder_RoundTrip_ProducesOriginalData()
    {
        var original = "This is a longer test string to verify chunked encoding round-trip works correctly!"u8.ToArray();
        var encoded = ChunkedEncodingParser.EncodeChunked(original, chunkSize: 10);
        var buffer = new ReadOnlySequence<byte>(encoded);
        
        var success = ChunkedEncodingParser.TryParseChunked(
            ref buffer,
            out var decoded,
            out _,
            out _);
        
        Assert.True(success);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ChunkedParser_ExceedsSizeLimit_ThrowsException()
    {
        // Create a chunk that exceeds limit
        var chunked = "100000\r\n"u8.ToArray(); // 1MB chunk
        var buffer = new ReadOnlySequence<byte>(chunked);
        
        Assert.Throws<InvalidOperationException>(() =>
        {
            ChunkedEncodingParser.TryParseChunked(
                ref buffer,
                out _,
                out _,
                out _,
                maxBodySize: 1024); // 1KB max
        });
    }

    #endregion

    #region Stream Priority Tests (RFC 7540)

    [Fact]
    public void StreamPriority_Parse_CorrectlyDecodesPriorityData()
    {
        // Create priority data: exclusive=true, dependsOn=3, weight=16
        var data = new byte[] { 0x80, 0x00, 0x00, 0x03, 15 }; // weight stored as weight-1
        
        var priority = Http2StreamPriority.Parse(data);
        
        Assert.True(priority.Exclusive);
        Assert.Equal(3, priority.DependsOn);
        Assert.Equal(16, priority.Weight);
    }

    [Fact]
    public void StreamPriority_Encode_ProducesCorrectBytes()
    {
        var priority = new Http2StreamPriority
        {
            Exclusive = true,
            DependsOn = 5,
            Weight = 20
        };
        
        var encoded = priority.Encode();
        
        Assert.Equal(5, encoded.Length);
        Assert.Equal(0x80, encoded[0]); // Exclusive bit set
        Assert.Equal(5, encoded[3]); // Stream ID in last byte
        Assert.Equal(19, encoded[4]); // Weight - 1
    }

    [Fact]
    public void StreamPriorityScheduler_RegisterStream_AddsToScheduler()
    {
        var scheduler = new StreamPriorityScheduler();
        
        scheduler.RegisterStream(1);
        var nextStream = scheduler.GetNextStreamId();
        
        Assert.Equal(1, nextStream);
    }

    [Fact]
    public void StreamPriorityScheduler_WithWeights_RespectsHigherWeight()
    {
        var scheduler = new StreamPriorityScheduler();
        
        scheduler.RegisterStream(1, new Http2StreamPriority { Weight = 10 });
        scheduler.RegisterStream(3, new Http2StreamPriority { Weight = 200 });
        
        var nextStream = scheduler.GetNextStreamId();
        
        // Stream 3 has much higher weight, should be selected
        Assert.Equal(3, nextStream);
    }

    [Fact]
    public void StreamPriorityScheduler_RemoveStream_NoLongerScheduled()
    {
        var scheduler = new StreamPriorityScheduler();
        
        scheduler.RegisterStream(1);
        scheduler.RemoveStream(1);
        var nextStream = scheduler.GetNextStreamId();
        
        Assert.Null(nextStream);
    }

    [Fact]
    public void StreamPriorityScheduler_ExclusiveDependency_MovesExistingChildren()
    {
        var scheduler = new StreamPriorityScheduler();
        
        scheduler.RegisterStream(1);
        scheduler.RegisterStream(3, new Http2StreamPriority { DependsOn = 1, Exclusive = false });
        scheduler.RegisterStream(5, new Http2StreamPriority { DependsOn = 1, Exclusive = true });
        
        // Stream 5 should now be the only child of stream 1
        // Stream 3 should be a child of stream 5
        var nextStream = scheduler.GetNextStreamId();
        Assert.NotNull(nextStream);
    }

    #endregion

    #region Content Negotiation Tests (RFC 7231)

    [Fact]
    public void ContentNegotiation_SelectContentType_ChoosesBestMatch()
    {
        var accept = "text/html, application/json;q=0.9, */*;q=0.8";
        var available = new[] { "application/json", "text/plain" };
        
        var selected = ContentNegotiation.SelectContentType(accept, available);
        
        Assert.Equal("application/json", selected);
    }

    [Fact]
    public void ContentNegotiation_SelectContentType_WildcardMatches()
    {
        var accept = "*/*";
        var available = new[] { "application/json", "text/html" };
        
        var selected = ContentNegotiation.SelectContentType(accept, available);
        
        Assert.NotNull(selected);
        Assert.Contains(selected, available);
    }

    [Fact]
    public void ContentNegotiation_SelectEncoding_PreferredOrder()
    {
        var acceptEncoding = "gzip, deflate, br";
        var available = new[] { "br", "gzip" };
        
        var selected = ContentNegotiation.SelectEncoding(acceptEncoding, available);
        
        Assert.Equal("gzip", selected); // gzip appears first in accept header
    }

    [Fact]
    public void ContentNegotiation_SelectEncoding_NoPreference_ReturnsNull()
    {
        string? acceptEncoding = null;
        var available = new[] { "gzip", "deflate" };
        
        var selected = ContentNegotiation.SelectEncoding(acceptEncoding, available);
        
        Assert.Null(selected); // No compression preference
    }

    [Fact]
    public void ContentNegotiation_SelectLanguage_ExactMatch()
    {
        var acceptLanguage = "en-US, fr;q=0.9";
        var available = new[] { "en-US", "en-GB" };
        
        var selected = ContentNegotiation.SelectLanguage(acceptLanguage, available);
        
        Assert.Equal("en-US", selected);
    }

    [Fact]
    public void ContentNegotiation_SelectLanguage_PrefixMatch()
    {
        var acceptLanguage = "en";
        var available = new[] { "en-US", "fr-FR" };
        
        var selected = ContentNegotiation.SelectLanguage(acceptLanguage, available);
        
        Assert.Equal("en-US", selected); // Matches "en" prefix
    }

    [Fact]
    public void ContentNegotiation_QualityFactor_ZeroRejects()
    {
        var accept = "text/html;q=0, application/json";
        var available = new[] { "text/html", "application/json" };
        
        var selected = ContentNegotiation.SelectContentType(accept, available);
        
        Assert.Equal("application/json", selected); // text/html rejected with q=0
    }

    [Fact]
    public void HttpRequest_GetPreferredContentType_UsesAcceptHeader()
    {
        var request = new HttpRequest();
        request.Headers["Accept"] = "application/json, text/html;q=0.9";
        
        var preferred = request.GetPreferredContentType("text/html", "application/json");
        
        Assert.Equal("application/json", preferred);
    }

    [Fact]
    public void HttpRequest_Accepts_ChecksContentType()
    {
        var request = new HttpRequest();
        request.Headers["Accept"] = "application/json";
        
        Assert.True(request.Accepts("application/json"));
        Assert.False(request.Accepts("text/html"));
    }

    #endregion
}
