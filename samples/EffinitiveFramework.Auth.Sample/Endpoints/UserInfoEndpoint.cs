using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authentication;

namespace EffinitiveFramework.Auth.Sample.Endpoints;

// ============================================
// User Info Endpoint - Get current user info
// ============================================

public class UserInfoEndpoint : AsyncEndpointBase<EmptyRequest, object>
{
    protected override string Route => "/me";
    protected override string Method => "GET";

    public override async Task<object> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var user = HttpContext?.User;
        
        if (user == null || !user.IsAuthenticated)
        {
            return new
            {
                authenticated = false,
                message = "Not authenticated. Access this endpoint with a Bearer token."
            };
        }

        return new
        {
            authenticated = true,
            name = user.Name,
            authenticationType = user.AuthenticationType,
            claims = user.Claims.Select(c => new
            {
                type = c.Type,
                value = c.Value
            }).ToList(),
            roles = user.FindAll(Claim.Types.Role).Select(c => c.Value).ToList()
        };
    }
}
