using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// GET /echo - Echoes all request headers back as plain text.
/// Used by HTTP compliance probes to verify header handling.
/// </summary>
public class EchoEndpoint : NoRequestEndpointBase<RawResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/echo";

    public override ValueTask<RawResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();
        if (HttpContext?.Headers != null)
        {
            foreach (var header in HttpContext.Headers)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
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

/// <summary>
/// POST /echo - Echoes all request headers back as plain text.
/// </summary>
public class EchoPostEndpoint : NoRequestEndpointBase<RawResponse>
{
    protected override string Method => "POST";
    protected override string Route => "/echo";

    public override ValueTask<RawResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();
        if (HttpContext?.Headers != null)
        {
            foreach (var header in HttpContext.Headers)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
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
