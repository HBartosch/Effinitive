using EffinitiveFramework.Core;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example endpoint that returns HTML content
/// Demonstrates ContentType customization with async I/O
/// </summary>
public class HtmlEndpoint : NoRequestAsyncEndpointBase<string>
{
    protected override string Method => "GET";
    protected override string Route => "/api/html";
    
    // Override ContentType to return HTML
    protected override string ContentType => "text/html";

    public override async Task<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        // Simulate async I/O (e.g., reading from database or file)
        await Task.Delay(10, cancellationToken);
        
        return @"<!DOCTYPE html>
<html>
<head>
    <title>EffinitiveFramework</title>
</head>
<body>
    <h1>Hello from HTML endpoint!</h1>
    <p>This demonstrates custom ContentType support.</p>
</body>
</html>";
    }
}
