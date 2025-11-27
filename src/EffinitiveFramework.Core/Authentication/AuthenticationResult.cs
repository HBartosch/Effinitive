namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Represents the result of an authentication attempt
/// </summary>
public sealed class AuthenticationResult
{
    /// <summary>
    /// Gets whether authentication was successful
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the authenticated user principal (null if authentication failed)
    /// </summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>
    /// Gets the failure reason (null if authentication succeeded)
    /// </summary>
    public string? FailureReason { get; }

    private AuthenticationResult(bool succeeded, ClaimsPrincipal? principal, string? failureReason)
    {
        Succeeded = succeeded;
        Principal = principal;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Creates a successful authentication result
    /// </summary>
    public static AuthenticationResult Success(ClaimsPrincipal principal)
    {
        if (principal == null)
            throw new ArgumentNullException(nameof(principal));

        return new AuthenticationResult(true, principal, null);
    }

    /// <summary>
    /// Creates a failed authentication result
    /// </summary>
    public static AuthenticationResult Fail(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "Authentication failed";

        return new AuthenticationResult(false, null, reason);
    }

    /// <summary>
    /// Creates a result indicating no authentication credentials were provided
    /// </summary>
    public static AuthenticationResult NoCredentials()
    {
        return new AuthenticationResult(false, null, "No authentication credentials provided");
    }
}
