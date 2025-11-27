using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authentication;
using EffinitiveFramework.Core.Authorization;

namespace EffinitiveFramework.Auth.Sample.Endpoints;

// ============================================
// Admin Endpoint - Requires Admin role
// ============================================

[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminEndpoint : AsyncEndpointBase<EmptyRequest, object>
{
    protected override string Route => "/admin";
    protected override string Method => "GET";

    public override async Task<object> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var user = HttpContext?.User;
        
        return new
        {
            message = "Welcome to the admin area!",
            description = "Only users with Admin or SuperAdmin role can access this",
            user = new
            {
                name = user?.Name,
                roles = user?.FindAll(Claim.Types.Role).Select(c => c.Value).ToList()
            },
            timestamp = DateTime.UtcNow
        };
    }
}
