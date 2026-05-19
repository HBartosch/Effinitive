using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Authentication;

/// <summary>
/// Delegate for validating API keys and returning claims
/// </summary>
public delegate ValueTask<AuthenticationResult> ApiKeyValidator(string apiKey, CancellationToken cancellationToken);

/// <summary>
/// Options for API key authentication
/// </summary>
public sealed class ApiKeyAuthenticationOptions
{
    /// <summary>
    /// The header name to look for the API key (default: "X-API-Key")
    /// </summary>
    public string HeaderName { get; set; } = "X-API-Key";

    /// <summary>
    /// The query string parameter name to look for the API key (optional)
    /// </summary>
    public string? QueryStringParameterName { get; set; }

    /// <summary>
    /// The validator function to validate API keys
    /// </summary>
    public ApiKeyValidator Validator { get; set; } = null!;

    /// <summary>
    /// Whether to allow API key in query string (default: false for security)
    /// </summary>
    public bool AllowQueryString { get; set; } = false;
}

/// <summary>
/// API key authentication handler
/// </summary>
public sealed class ApiKeyAuthenticationHandler : IAuthenticationHandler
{
    private readonly ApiKeyAuthenticationOptions _options;

    public ApiKeyAuthenticationHandler(ApiKeyAuthenticationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.Validator == null)
            throw new ArgumentException("Validator is required for API key authentication", nameof(options));
    }

    public async ValueTask<AuthenticationResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        string? apiKey = null;

        // Try to get from header first
        if (request.Headers.TryGetValue(_options.HeaderName, out var headerValue))
        {
            apiKey = headerValue;
        }
        // Try query string if enabled and not found in header
        else if (_options.AllowQueryString && !string.IsNullOrWhiteSpace(_options.QueryStringParameterName))
        {
            request.Query.TryGetValue(_options.QueryStringParameterName, out apiKey);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticationResult.NoCredentials();
        }

        try
        {
            // Call custom validator
            return await _options.Validator(apiKey, cancellationToken);
        }
        catch (Exception ex)
        {
            return AuthenticationResult.Fail($"API key validation failed: {ex.Message}");
        }
    }
}
