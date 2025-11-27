namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Content negotiation helper for RFC 7231 §5.3
/// Processes Accept, Accept-Encoding, Accept-Language headers
/// </summary>
public static class ContentNegotiation
{
    /// <summary>
    /// Parse Accept header and find best match
    /// Format: "text/html, application/json;q=0.9, */*;q=0.8"
    /// </summary>
    public static string? SelectContentType(string? acceptHeader, string[] availableTypes)
    {
        if (string.IsNullOrWhiteSpace(acceptHeader))
            return availableTypes.FirstOrDefault();

        var accepted = ParseAcceptHeader(acceptHeader);
        
        // Find best match based on quality factor
        foreach (var (mediaType, quality) in accepted.OrderByDescending(x => x.quality))
        {
            if (mediaType == "*/*")
                return availableTypes.FirstOrDefault();

            var wildcardIndex = mediaType.IndexOf('*');
            if (wildcardIndex > 0)
            {
                // Type wildcard: "text/*"
                var prefix = mediaType.Substring(0, wildcardIndex);
                var match = availableTypes.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
            else
            {
                // Exact match
                var match = availableTypes.FirstOrDefault(t => t.Equals(mediaType, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }

        return null; // No acceptable type
    }

    /// <summary>
    /// Parse Accept-Encoding header and select best encoding
    /// Format: "gzip, deflate, br"
    /// </summary>
    public static string? SelectEncoding(string? acceptEncodingHeader, string[] availableEncodings)
    {
        if (string.IsNullOrWhiteSpace(acceptEncodingHeader))
            return null; // No compression preferred

        var accepted = ParseAcceptHeader(acceptEncodingHeader);

        // Check for explicit identity rejection
        var identityEntry = accepted.FirstOrDefault(x => x.item.Equals("identity", StringComparison.OrdinalIgnoreCase));
        var identityAllowed = identityEntry.quality != 0;

        foreach (var (encoding, quality) in accepted.OrderByDescending(x => x.quality))
        {
            if (quality == 0)
                continue; // Explicitly not acceptable

            if (encoding == "*")
            {
                // Any encoding is acceptable
                return availableEncodings.FirstOrDefault();
            }

            var match = availableEncodings.FirstOrDefault(e => e.Equals(encoding, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // If identity is allowed (default), return null (no compression)
        return identityAllowed ? null : availableEncodings.FirstOrDefault();
    }

    /// <summary>
    /// Parse Accept-Language header and select best language
    /// Format: "en-US, en;q=0.9, fr;q=0.8"
    /// </summary>
    public static string? SelectLanguage(string? acceptLanguageHeader, string[] availableLanguages)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguageHeader))
            return availableLanguages.FirstOrDefault();

        var accepted = ParseAcceptHeader(acceptLanguageHeader);

        foreach (var (language, quality) in accepted.OrderByDescending(x => x.quality))
        {
            if (quality == 0)
                continue;

            if (language == "*")
                return availableLanguages.FirstOrDefault();

            // Try exact match first
            var exactMatch = availableLanguages.FirstOrDefault(l => l.Equals(language, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
                return exactMatch;

            // Try language prefix match (e.g., "en" matches "en-US")
            var hyphenIndex = language.IndexOf('-');
            if (hyphenIndex > 0)
            {
                var languagePrefix = language.Substring(0, hyphenIndex);
                var prefixMatch = availableLanguages.FirstOrDefault(l => 
                    l.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase));
                if (prefixMatch != null)
                    return prefixMatch;
            }
            else
            {
                // Language without region - match any variant
                var variantMatch = availableLanguages.FirstOrDefault(l => 
                    l.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals(language, StringComparison.OrdinalIgnoreCase));
                if (variantMatch != null)
                    return variantMatch;
            }
        }

        return null; // No acceptable language
    }

    private static List<(string item, double quality)> ParseAcceptHeader(string header)
    {
        var result = new List<(string, double)>();

        var items = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var item in items)
        {
            var parts = item.Split(';', StringSplitOptions.TrimEntries);
            var value = parts[0];
            var quality = 1.0;

            // Parse quality factor (q=0.9)
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(parts[i].Substring(2), out var q))
                    {
                        quality = Math.Clamp(q, 0.0, 1.0);
                    }
                    break;
                }
            }

            result.Add((value, quality));
        }

        return result;
    }
}

/// <summary>
/// Extension methods for HttpRequest to access content negotiation
/// </summary>
public static class ContentNegotiationExtensions
{
    /// <summary>
    /// Get the best content type based on Accept header
    /// </summary>
    public static string? GetPreferredContentType(this HttpRequest request, params string[] availableTypes)
    {
        var acceptHeader = request.Headers.TryGetValue("Accept", out var accept) ? accept : null;
        return ContentNegotiation.SelectContentType(acceptHeader, availableTypes);
    }

    /// <summary>
    /// Get the preferred encoding based on Accept-Encoding header
    /// </summary>
    public static string? GetPreferredEncoding(this HttpRequest request, params string[] availableEncodings)
    {
        var acceptEncodingHeader = request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) ? acceptEncoding : null;
        return ContentNegotiation.SelectEncoding(acceptEncodingHeader, availableEncodings);
    }

    /// <summary>
    /// Get the preferred language based on Accept-Language header
    /// </summary>
    public static string? GetPreferredLanguage(this HttpRequest request, params string[] availableLanguages)
    {
        var acceptLanguageHeader = request.Headers.TryGetValue("Accept-Language", out var acceptLanguage) ? acceptLanguage : null;
        return ContentNegotiation.SelectLanguage(acceptLanguageHeader, availableLanguages);
    }

    /// <summary>
    /// Check if client accepts a specific content type
    /// </summary>
    public static bool Accepts(this HttpRequest request, string contentType)
    {
        var acceptHeader = request.Headers.TryGetValue("Accept", out var accept) ? accept : null;
        if (string.IsNullOrWhiteSpace(acceptHeader))
            return true; // No preference means accept all

        return ContentNegotiation.SelectContentType(acceptHeader, new[] { contentType }) != null;
    }
}
