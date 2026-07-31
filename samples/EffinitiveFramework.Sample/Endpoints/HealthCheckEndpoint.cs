using EffinitiveFramework.Core;
using EffinitiveFramework.Core.OpenApi;
using EffinitiveFramework.Core.RateLimiting;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example of NoRequestEndpointBase - simple GET endpoint without request body
/// Returns synchronously from in-memory data
/// </summary>
/// <remarks>
/// Exempt from rate limiting: a load balancer polling this must keep getting an answer even while the
/// caller's address is being throttled for everything else.
/// </remarks>
[DisableRateLimit]
[OpenApiOperation(Summary = "Health check", Tags = "Health")]
public class HealthCheckEndpoint : NoRequestEndpointBase<HealthCheckResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/health";

    public override ValueTask<HealthCheckResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        var response = new HealthCheckResponse
        {
            Status = "Healthy",
            Timestamp = DateTime.UtcNow,
            Version = "1.1.0",
            Uptime = TimeSpan.FromSeconds(Environment.TickCount64 / 1000.0)
        };

        return ValueTask.FromResult(response);
    }
}

public class HealthCheckResponse
{
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Version { get; set; } = string.Empty;
    public TimeSpan Uptime { get; set; }
}
