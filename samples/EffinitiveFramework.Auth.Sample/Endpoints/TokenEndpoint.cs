using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authorization;

namespace EffinitiveFramework.Auth.Sample.Endpoints;

// ============================================
// Token Generation Endpoint
// ============================================

public record TokenRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[AllowAnonymous]
public class TokenEndpoint : AsyncEndpointBase<TokenRequest, object>
{
    protected override string Route => "/auth/token";
    protected override string Method => "POST";

    public override async Task<object> HandleAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        // Simple credential validation (in production, check against database)
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return new
            {
                success = false,
                error = "Username and password are required"
            };
        }

        // Demo credentials
        string role = request.Username.ToLower() switch
        {
            "admin" when request.Password == "admin123" => "Admin",
            "user" when request.Password == "user123" => "User",
            _ => null
        };

        if (role == null)
        {
            return new
            {
                success = false,
                error = "Invalid username or password",
                hint = "Try: admin/admin123 or user/user123"
            };
        }

        // Generate JWT token
        var secretKey = "my-super-secret-key-that-is-at-least-32-characters-long!";
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, request.Username),
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Name, request.Username),
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Email, $"{request.Username}@example.com"),
            new System.Security.Claims.Claim("role", role),
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "EffinitiveFramework",
            audience: "EffinitiveFrameworkAPI",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return new
        {
            success = true,
            token = tokenString,
            expiresIn = 3600, // seconds
            username = request.Username,
            role = role,
            message = "Token generated successfully. Use it in Authorization header: 'Bearer {token}'"
        };
    }
}
