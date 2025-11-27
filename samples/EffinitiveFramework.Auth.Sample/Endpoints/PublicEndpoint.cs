using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authorization;

namespace EffinitiveFramework.Auth.Sample.Endpoints;

// ============================================
// Public Endpoint - No authentication needed
// ============================================

public record EmptyRequest;

[AllowAnonymous]
public class PublicEndpoint : AsyncEndpointBase<EmptyRequest, object>
{
    protected override string Route => "/public";
    protected override string Method => "GET";

    public override async Task<object> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new
        {
            message = "This is a public endpoint!",
            description = "Anyone can access this without authentication",
            timestamp = DateTime.UtcNow
        };
    }
}
