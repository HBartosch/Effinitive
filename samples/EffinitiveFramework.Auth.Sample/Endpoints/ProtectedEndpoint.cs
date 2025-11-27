using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authentication;
using EffinitiveFramework.Core.Authorization;

namespace EffinitiveFramework.Auth.Sample.Endpoints;

// ============================================
// Protected Endpoint - Requires authentication
// ============================================

[Authorize]
public class ProtectedEndpoint : AsyncEndpointBase<EmptyRequest, object>
{
    protected override string Route => "/protected";
    protected override string Method => "GET";

    public override async Task<object> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var user = HttpContext?.User;
        
        return new
        {
            message = "This is a protected endpoint!",
            description = "You need to be authenticated to access this",
            user = new
            {
                name = user?.Name,
                authenticated = user?.IsAuthenticated ?? false,
                authenticationType = user?.AuthenticationType,
                claims = user?.Claims.Select(c => new { c.Type, c.Value }).ToList()
            },
            timestamp = DateTime.UtcNow
        };
    }
}
