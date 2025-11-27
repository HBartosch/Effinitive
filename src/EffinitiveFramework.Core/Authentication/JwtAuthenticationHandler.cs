using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Options for JWT authentication
/// </summary>
public sealed class JwtAuthenticationOptions
{
    /// <summary>
    /// The secret key used to validate JWT signatures (required for HMAC)
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The issuer to validate (optional)
    /// </summary>
    public string? ValidIssuer { get; set; }

    /// <summary>
    /// The audience to validate (optional)
    /// </summary>
    public string? ValidAudience { get; set; }

    /// <summary>
    /// Whether to validate the token lifetime (default: true)
    /// </summary>
    public bool ValidateLifetime { get; set; } = true;

    /// <summary>
    /// Whether to validate the issuer (default: false if ValidIssuer not set)
    /// </summary>
    public bool ValidateIssuer { get; set; }

    /// <summary>
    /// Whether to validate the audience (default: false if ValidAudience not set)
    /// </summary>
    public bool ValidateAudience { get; set; }

    /// <summary>
    /// Clock skew for expiration validation (default: 5 minutes)
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The header name to look for the token (default: "Authorization")
    /// </summary>
    public string HeaderName { get; set; } = "Authorization";

    /// <summary>
    /// The scheme prefix in the header (default: "Bearer")
    /// </summary>
    public string Scheme { get; set; } = "Bearer";
}

/// <summary>
/// JWT authentication handler using System.IdentityModel.Tokens.Jwt
/// </summary>
public sealed class JwtAuthenticationHandler : IAuthenticationHandler
{
    private readonly JwtAuthenticationOptions _options;
    private readonly TokenValidationParameters _validationParameters;
    private readonly JwtSecurityTokenHandler _tokenHandler;

    public JwtAuthenticationHandler(JwtAuthenticationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ArgumentException("SecretKey is required for JWT authentication", nameof(options));

        _tokenHandler = new JwtSecurityTokenHandler();

        // Build validation parameters
        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(_options.SecretKey)),
            ValidateIssuer = _options.ValidateIssuer,
            ValidIssuer = _options.ValidIssuer,
            ValidateAudience = _options.ValidateAudience,
            ValidAudience = _options.ValidAudience,
            ValidateLifetime = _options.ValidateLifetime,
            ClockSkew = _options.ClockSkew
        };
    }

    public ValueTask<AuthenticationResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        // Extract token from header
        if (!request.Headers.TryGetValue(_options.HeaderName, out var authHeader))
        {
            return ValueTask.FromResult(AuthenticationResult.NoCredentials());
        }

        // Check for Bearer scheme
        if (!authHeader.StartsWith(_options.Scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(AuthenticationResult.Fail($"Invalid authentication scheme. Expected '{_options.Scheme}'"));
        }

        var token = authHeader.Substring(_options.Scheme.Length + 1).Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Token is empty"));
        }

        try
        {
            // Validate token
            var claimsPrincipal = _tokenHandler.ValidateToken(token, _validationParameters, out var validatedToken);

            // Convert System.Security.Claims to our Claim type
            var claims = claimsPrincipal.Claims
                .Select(c => new Authentication.Claim(c.Type, c.Value))
                .ToList();

            var principal = new ClaimsPrincipal(claims, "JWT");

            return ValueTask.FromResult(AuthenticationResult.Success(principal));
        }
        catch (SecurityTokenExpiredException)
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Token has expired"));
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Invalid token signature"));
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Invalid token issuer"));
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Invalid token audience"));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(AuthenticationResult.Fail($"Token validation failed: {ex.Message}"));
        }
    }
}
