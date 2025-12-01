using EffinitiveFramework.Core;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example of NoRequestEndpointBase - simple GET endpoint without request body
/// Returns synchronously from in-memory data
/// </summary>
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
