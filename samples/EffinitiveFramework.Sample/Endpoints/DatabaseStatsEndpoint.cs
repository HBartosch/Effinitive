using EffinitiveFramework.Core;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example of NoRequestAsyncEndpointBase - GET endpoint without request body that performs async I/O
/// Simulates database query to fetch statistics
/// </summary>
public class DatabaseStatsEndpoint : NoRequestAsyncEndpointBase<DatabaseStatsResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/stats/database";

    public override async Task<DatabaseStatsResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        // Simulate database query
        await Task.Delay(50, cancellationToken);

        var response = new DatabaseStatsResponse
        {
            TotalRecords = Random.Shared.Next(10000, 100000),
            ActiveConnections = Random.Shared.Next(1, 50),
            QueryCount = Random.Shared.Next(1000, 10000),
            AverageResponseTime = TimeSpan.FromMilliseconds(Random.Shared.Next(10, 100)),
            LastUpdated = DateTime.UtcNow
        };

        return response;
    }
}

public class DatabaseStatsResponse
{
    public int TotalRecords { get; set; }
    public int ActiveConnections { get; set; }
    public int QueryCount { get; set; }
    public TimeSpan AverageResponseTime { get; set; }
    public DateTime LastUpdated { get; set; }
}
