using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// GET /cookie - Parses Cookie header and returns individual cookies.
/// Used by HTTP compliance probes to verify cookie parsing.
/// </summary>
public class CookieEndpoint : NoRequestEndpointBase<RawResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/cookie";

    public override ValueTask<RawResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();

        if (HttpContext != null)
        {
            foreach (var cookie in HttpContext.Cookies)
            {
                sb.Append(cookie.Key).Append('=').Append(cookie.Value).Append("\r\n");
            }
        }

        return ValueTask.FromResult(new RawResponse
        {
            StatusCode = 200,
            ContentType = "text/plain",
            Body = System.Text.Encoding.UTF8.GetBytes(sb.ToString())
        });
    }
}
