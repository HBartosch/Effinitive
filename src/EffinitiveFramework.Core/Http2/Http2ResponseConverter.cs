using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Converts HTTP responses to HTTP/2 format
/// </summary>
public static class Http2ResponseConverter
{
    /// <summary>
    /// Convert HTTP/1.1 response to HTTP/2 headers
    /// </summary>
    public static List<(string name, string value)> ConvertToHttp2Headers(HttpResponse response)
    {
        var headers = new List<(string name, string value)>
        {
            (":status", response.StatusCode.ToString())
        };
        
        // Add regular headers
        foreach (var (name, value) in response.Headers)
        {
            var lowerName = name.ToLowerInvariant();
            
            // Skip connection-specific headers
            if (lowerName == "connection" || 
                lowerName == "keep-alive" || 
                lowerName == "proxy-connection" ||
                lowerName == "transfer-encoding" ||
                lowerName == "upgrade")
            {
                continue;
            }
            
            headers.Add((lowerName, value));
        }
        
        return headers;
    }
}
