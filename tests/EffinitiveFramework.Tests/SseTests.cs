using EffinitiveFramework.Core.Http.ServerSentEvents;
using Xunit;

namespace EffinitiveFramework.Tests;

public class SseTests
{
    [Fact]
    public void SseEvent_SimpleMessage_FormatsCorrectly()
    {
        var evt = SseEvent.Message("Hello World");
        var bytes = evt.ToBytes();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        
        Assert.Contains("data: Hello World\n", text);
        Assert.EndsWith("\n\n", text);
    }

    [Fact]
    public void SseEvent_WithEventType_FormatsCorrectly()
    {
        var evt = SseEvent.Typed("custom-event", "Event data");
        var bytes = evt.ToBytes();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        
        Assert.Contains("event: custom-event\n", text);
        Assert.Contains("data: Event data\n", text);
    }

    [Fact]
    public void SseEvent_WithId_FormatsCorrectly()
    {
        var evt = new SseEvent 
        { 
            Id = "123", 
            Data = "Test data" 
        };
        var bytes = evt.ToBytes();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        
        Assert.Contains("id: 123\n", text);
        Assert.Contains("data: Test data\n", text);
    }

    [Fact]
    public void SseEvent_WithRetry_FormatsCorrectly()
    {
        var evt = new SseEvent 
        { 
            Retry = 5000, 
            Data = "Retry test" 
        };
        var bytes = evt.ToBytes();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        
        Assert.Contains("retry: 5000\n", text);
        Assert.Contains("data: Retry test\n", text);
    }

    [Fact]
    public void SseEvent_MultilineData_FormatsCorrectly()
    {
        var evt = SseEvent.Message("Line 1\nLine 2\nLine 3");
        var bytes = evt.ToBytes();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        
        Assert.Contains("data: Line 1\n", text);
        Assert.Contains("data: Line 2\n", text);
        Assert.Contains("data: Line 3\n", text);
    }

    [Fact]
    public void SseEvent_Comment_FormatsCorrectly()
    {
        var bytes = SseEvent.Comment("keep-alive");
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        
        Assert.Equal(": keep-alive\n\n", text);
    }

    [Fact]
    public async Task SseStream_WritesEvent()
    {
        using var memoryStream = new MemoryStream();
        await using var sseStream = new SseStream(memoryStream);
        
        await sseStream.WriteAsync("Test message");
        
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("data: Test message\n", content);
    }

    [Fact]
    public async Task SseStream_WritesTypedEvent()
    {
        using var memoryStream = new MemoryStream();
        await using var sseStream = new SseStream(memoryStream);
        
        await sseStream.WriteAsync("status", "connected");
        
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("event: status\n", content);
        Assert.Contains("data: connected\n", content);
    }

    [Fact]
    public async Task SseStream_WritesJson()
    {
        using var memoryStream = new MemoryStream();
        await using var sseStream = new SseStream(memoryStream);
        
        var data = new { name = "Test", value = 123 };
        await sseStream.WriteJsonAsync(data);
        
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("data: {", content);
        Assert.Contains("\"name\":\"Test\"", content);
        Assert.Contains("\"value\":123", content);
    }

    [Fact]
    public async Task SseStream_WritesKeepAlive()
    {
        using var memoryStream = new MemoryStream();
        await using var sseStream = new SseStream(memoryStream);
        
        await sseStream.WriteKeepAliveAsync();
        
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.StartsWith(": keep-alive", content);
    }

    [Fact]
    public async Task SseStream_MultipleWrites_Sequential()
    {
        using var memoryStream = new MemoryStream();
        await using var sseStream = new SseStream(memoryStream);
        
        await sseStream.WriteAsync("Message 1");
        await sseStream.WriteAsync("Message 2");
        await sseStream.WriteAsync("Message 3");
        
        memoryStream.Position = 0;
        var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();
        
        Assert.Contains("data: Message 1\n", content);
        Assert.Contains("data: Message 2\n", content);
        Assert.Contains("data: Message 3\n", content);
    }
}
