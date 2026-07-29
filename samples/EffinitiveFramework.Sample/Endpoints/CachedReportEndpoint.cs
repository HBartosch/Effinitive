using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Caching;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// GET /api/report - demonstrates v2.3 response caching.
/// <para>
/// The handler sleeps to stand in for expensive work and stamps the response with the time it ran.
/// Request it twice within 30 seconds and the second response is byte-identical, arrives immediately,
/// and carries an <c>Age</c> header — the handler never ran. A POST to the same path invalidates the
/// entry, so the next GET is recomputed.
/// </para>
/// </summary>
[ResponseCache(Duration = 30)]
public class CachedReportEndpoint : NoRequestAsyncEndpointBase<CachedReportResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/report";

    public override async Task<CachedReportResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        // Stand-in for a slow query or an upstream call.
        await Task.Delay(250, cancellationToken);

        return new CachedReportResponse
        {
            GeneratedAt = DateTime.UtcNow,
            TotalOrders = Random.Shared.Next(10_000, 99_999),
            Revenue = Math.Round(Random.Shared.NextDouble() * 1_000_000, 2)
        };
    }
}

/// <summary>
/// POST /api/report - any successful write to this path evicts every cached representation of it,
/// so the next GET recomputes (RFC 9111 §4.4).
/// </summary>
public class RefreshReportEndpoint : NoRequestAsyncEndpointBase<RefreshReportResponse>
{
    protected override string Method => "POST";
    protected override string Route => "/api/report";

    public override Task<RefreshReportResponse> HandleAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new RefreshReportResponse { Invalidated = true });
}

public class CachedReportResponse
{
    public DateTime GeneratedAt { get; set; }
    public int TotalOrders { get; set; }
    public double Revenue { get; set; }
}

public class RefreshReportResponse
{
    public bool Invalidated { get; set; }
}
