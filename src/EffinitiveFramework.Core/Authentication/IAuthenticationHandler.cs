using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Interface for authentication handlers that validate requests
/// </summary>
public interface IAuthenticationHandler
{
    /// <summary>
    /// Authenticates the request and returns the result
    /// </summary>
    ValueTask<AuthenticationResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken = default);
}
