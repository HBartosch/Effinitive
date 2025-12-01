using EffinitiveFramework.Core;
using Xunit;

namespace EffinitiveFramework.Tests;

public class NoRequestEndpointTests
{
    [Fact]
    public async Task NoRequestEndpointBase_ShouldHandleRequestWithoutBody()
    {
        // Arrange
        var endpoint = new TestNoRequestEndpoint();

        // Act
        var result = await endpoint.HandleAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Response", result.Message);
    }

    [Fact]
    public async Task NoRequestEndpointBase_ShouldWorkWithCancellationToken()
    {
        // Arrange
        var endpoint = new TestNoRequestEndpoint();
        var cts = new CancellationTokenSource();

        // Act
        var result = await endpoint.HandleAsync(cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Response", result.Message);
    }

    [Fact]
    public async Task NoRequestAsyncEndpointBase_ShouldHandleRequestWithoutBody()
    {
        // Arrange
        var endpoint = new TestNoRequestAsyncEndpoint();

        // Act
        var result = await endpoint.HandleAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Async Test Response", result.Message);
        Assert.True(result.WasAsync);
    }

    [Fact]
    public async Task NoRequestAsyncEndpointBase_ShouldRespectCancellation()
    {
        // Arrange
        var endpoint = new TestNoRequestAsyncEndpoint();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await endpoint.HandleAsync(cts.Token)
        );
    }

    [Fact]
    public async Task NoRequestEndpointBase_ShouldImplementIEndpointInterface()
    {
        // Arrange
        IEndpoint<EmptyRequest, TestResponse> endpoint = new TestNoRequestEndpoint();

        // Act
        var result = await endpoint.HandleAsync(default, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Response", result.Message);
    }

    [Fact]
    public async Task NoRequestAsyncEndpointBase_ShouldImplementIAsyncEndpointInterface()
    {
        // Arrange
        IAsyncEndpoint<EmptyRequest, TestAsyncResponse> endpoint = new TestNoRequestAsyncEndpoint();

        // Act
        var result = await endpoint.HandleAsync(default, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Async Test Response", result.Message);
        Assert.True(result.WasAsync);
    }

    [Fact]
    public void NoRequestEndpointBase_ShouldAllowCustomContentType()
    {
        // Arrange
        var endpoint = new TestPlainTextEndpoint();

        // Act - Access the ContentType property via reflection (it's protected)
        var contentTypeProperty = endpoint.GetType().GetProperty("ContentType",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var contentType = contentTypeProperty?.GetValue(endpoint) as string;

        // Assert
        Assert.Equal("text/plain", contentType);
    }

    [Fact]
    public void NoRequestAsyncEndpointBase_ShouldAllowCustomContentType()
    {
        // Arrange
        var endpoint = new TestHtmlEndpoint();

        // Act - Access the ContentType property via reflection (it's protected)
        var contentTypeProperty = endpoint.GetType().GetProperty("ContentType",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var contentType = contentTypeProperty?.GetValue(endpoint) as string;

        // Assert
        Assert.Equal("text/html", contentType);
    }
}

// Test implementations
internal class TestNoRequestEndpoint : NoRequestEndpointBase<TestResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/test/empty";

    public override ValueTask<TestResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new TestResponse { Message = "Test Response" });
    }
}

internal class TestNoRequestAsyncEndpoint : NoRequestAsyncEndpointBase<TestAsyncResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/test/empty-async";

    public override async Task<TestAsyncResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken);
        return new TestAsyncResponse { Message = "Async Test Response", WasAsync = true };
    }
}

internal class TestPlainTextEndpoint : NoRequestEndpointBase<string>
{
    protected override string Method => "GET";
    protected override string Route => "/test/plain";
    protected override string ContentType => "text/plain";

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult("Plain text response");
    }
}

internal class TestHtmlEndpoint : NoRequestAsyncEndpointBase<string>
{
    protected override string Method => "GET";
    protected override string Route => "/test/html";
    protected override string ContentType => "text/html";

    public override async Task<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(1, cancellationToken);
        return "<html><body>HTML response</body></html>";
    }
}

internal class TestResponse
{
    public string Message { get; set; } = string.Empty;
}

internal class TestAsyncResponse
{
    public string Message { get; set; } = string.Empty;
    public bool WasAsync { get; set; }
}
