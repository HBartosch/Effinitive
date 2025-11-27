namespace EffinitiveFramework.Core.Authorization;

/// <summary>
/// Specifies that the class or method requires authorization
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class AuthorizeAttribute : Attribute
{
    /// <summary>
    /// Gets or sets comma-separated list of roles that are authorized to access the resource
    /// </summary>
    public string? Roles { get; set; }

    /// <summary>
    /// Gets or sets the policy name that determines access to the resource
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Creates a new instance of AuthorizeAttribute
    /// </summary>
    public AuthorizeAttribute()
    {
    }

    /// <summary>
    /// Creates a new instance of AuthorizeAttribute with specified roles
    /// </summary>
    public AuthorizeAttribute(string roles)
    {
        Roles = roles;
    }
}
