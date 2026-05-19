using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Base class for custom authentication handlers
/// </summary>
public abstract class CustomAuthenticationHandler : IAuthenticationHandler
{
    /// <summary>
    /// Authenticates the request and returns the result
    /// </summary>
    public abstract ValueTask<AuthenticationResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Helper method to create a successful result with claims
    /// </summary>
    protected AuthenticationResult Success(IEnumerable<Claim> claims, string authenticationType)
    {
        var principal = new ClaimsPrincipal(claims, authenticationType);
        return AuthenticationResult.Success(principal);
    }

    /// <summary>
    /// Helper method to create a failed result
    /// </summary>
    protected AuthenticationResult Fail(string reason)
    {
        return AuthenticationResult.Fail(reason);
    }

    /// <summary>
    /// Helper method to indicate no credentials were provided
    /// </summary>
    protected AuthenticationResult NoCredentials()
    {
        return AuthenticationResult.NoCredentials();
    }

    /// <summary>
    /// Helper method to extract header value
    /// </summary>
    protected string? GetHeader(HttpRequest request, string headerName)
    {
        return request.Headers.TryGetValue(headerName, out var value) ? value : null;
    }

    /// <summary>
    /// Helper method to extract query string parameter
    /// </summary>
    protected string? GetQueryParameter(HttpRequest request, string parameterName)
    {
        return request.Query.Get(parameterName);
    }
}
