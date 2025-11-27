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
        var queryIndex = request.Path.IndexOf('?');
        if (queryIndex < 0)
            return null;

        var queryString = request.Path.Substring(queryIndex + 1);
        var parameters = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);

        foreach (var param in parameters)
        {
            var parts = param.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }
}
