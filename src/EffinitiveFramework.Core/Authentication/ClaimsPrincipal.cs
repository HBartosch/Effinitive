namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Represents the identity of a user with associated claims
/// </summary>
public sealed class ClaimsPrincipal
{
    private readonly List<Claim> _claims;

    /// <summary>
    /// Gets all claims associated with this principal
    /// </summary>
    public IReadOnlyList<Claim> Claims => _claims;

    /// <summary>
    /// Gets whether the user is authenticated
    /// </summary>
    public bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the authentication type (e.g., "JWT", "ApiKey", "Custom")
    /// </summary>
    public string? AuthenticationType { get; }

    /// <summary>
    /// Gets the user's name from claims
    /// </summary>
    public string? Name => FindFirst(Claim.Types.Name)?.Value ?? 
                          FindFirst(Claim.Types.NameIdentifier)?.Value;

    /// <summary>
    /// Creates an anonymous (unauthenticated) principal
    /// </summary>
    public ClaimsPrincipal()
    {
        _claims = new List<Claim>();
        IsAuthenticated = false;
    }

    /// <summary>
    /// Creates an authenticated principal with claims
    /// </summary>
    public ClaimsPrincipal(IEnumerable<Claim> claims, string authenticationType)
    {
        if (string.IsNullOrWhiteSpace(authenticationType))
            throw new ArgumentException("Authentication type cannot be null or whitespace", nameof(authenticationType));

        _claims = new List<Claim>(claims ?? throw new ArgumentNullException(nameof(claims)));
        IsAuthenticated = true;
        AuthenticationType = authenticationType;
    }

    /// <summary>
    /// Finds the first claim with the specified type
    /// </summary>
    public Claim? FindFirst(string type)
    {
        return _claims.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds all claims with the specified type
    /// </summary>
    public IEnumerable<Claim> FindAll(string type)
    {
        return _claims.Where(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the user has the specified claim
    /// </summary>
    public bool HasClaim(string type, string value)
    {
        return _claims.Any(c => 
            c.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && 
            c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the user is in the specified role
    /// </summary>
    public bool IsInRole(string role)
    {
        // Check both short form "role" and Microsoft schema URI
        return HasClaim(Claim.Types.Role, role) || 
               HasClaim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", role);
    }

    /// <summary>
    /// Checks if the user is in any of the specified roles
    /// </summary>
    public bool IsInAnyRole(params string[] roles)
    {
        if (roles == null || roles.Length == 0)
            return false;

        return roles.Any(role => IsInRole(role));
    }

    /// <summary>
    /// Checks if the user is in all of the specified roles
    /// </summary>
    public bool IsInAllRoles(params string[] roles)
    {
        if (roles == null || roles.Length == 0)
            return false;

        return roles.All(role => IsInRole(role));
    }
}
