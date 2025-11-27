namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Represents a claim - a statement about a subject (e.g., name, email, role)
/// </summary>
public sealed class Claim
{
    /// <summary>
    /// The claim type (e.g., "name", "role", "email")
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// The claim value
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new claim
    /// </summary>
    public Claim(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Claim type cannot be null or whitespace", nameof(type));
        
        Type = type;
        Value = value ?? string.Empty;
    }

    /// <summary>
    /// Common claim types
    /// </summary>
    public static class Types
    {
        public const string Name = "name";
        public const string Email = "email";
        public const string Role = "role";
        public const string Subject = "sub";
        public const string NameIdentifier = "nameid";
        public const string UserId = "userid";
    }

    public override string ToString() => $"{Type}: {Value}";
}
