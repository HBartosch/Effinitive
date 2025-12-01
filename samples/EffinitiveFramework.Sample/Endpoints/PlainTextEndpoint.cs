using EffinitiveFramework.Core;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example endpoint that returns plain text instead of JSON
/// Demonstrates ContentType customization
/// </summary>
public class PlainTextEndpoint : NoRequestEndpointBase<string>
{
    protected override string Method => "GET";
    protected override string Route => "/api/plain";
    
    // Override ContentType to return plain text
    protected override string ContentType => "text/plain";

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult("Hello from plain text endpoint!");
    }
}
