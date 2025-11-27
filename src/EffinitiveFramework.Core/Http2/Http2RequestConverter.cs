using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.Http2;

/// <summary>
/// Converts HTTP/1.1 requests to HTTP/2 streams
/// </summary>
public static class Http2RequestConverter
{
    /// <summary>
    /// Convert HTTP/1.1 request to HTTP/2 pseudo-headers
    /// </summary>
    public static List<(string name, string value)> ConvertToHttp2Headers(HttpRequest request)
    {
        var headers = new List<(string name, string value)>
        {
            (":method", request.Method),
            (":path", request.Path),
            (":scheme", request.IsHttps ? "https" : "http"),
            (":authority", request.Headers.ContainsKey("host") ? request.Headers["host"] : "")
        };
        
        // Add regular headers (excluding pseudo-headers and connection-specific headers)
        foreach (var (name, value) in request.Headers)
        {
            var lowerName = name.ToLowerInvariant();
            
            // Skip connection-specific headers (HTTP/2 doesn't use these)
            if (lowerName == "connection" || 
                lowerName == "keep-alive" || 
                lowerName == "proxy-connection" ||
                lowerName == "transfer-encoding" ||
                lowerName == "upgrade" ||
                lowerName == "host")
            {
                continue;
            }
            
            headers.Add((lowerName, value));
        }
        
        return headers;
    }
    
    /// <summary>
    /// Convert HTTP/2 headers to HTTP/1.1 request
    /// </summary>
    public static HttpRequest ConvertToHttp1Request(List<(string name, string value)> headers, byte[] body)
    {
        var request = new HttpRequest();
        
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith(':'))
            {
                // Pseudo-header
                switch (name)
                {
                    case ":method":
                        request.Method = value;
                        break;
                    case ":path":
                        request.Path = value;
                        break;
                    case ":scheme":
                        request.IsHttps = value == "https";
                        break;
                    case ":authority":
                        request.Headers["host"] = value;
                        break;
                }
            }
            else
            {
                // Regular header
                request.Headers[name] = value;
            }
        }
        
        request.Body = body;
        request.ContentLength = body.Length;
        request.HttpVersion = "HTTP/2.0";
        
        return request;
    }
}
