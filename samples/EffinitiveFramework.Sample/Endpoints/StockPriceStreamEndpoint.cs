using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Http.ServerSentEvents;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example of strongly-typed SSE endpoint with request and response types
/// </summary>
public class StockPriceStreamEndpoint : SseEndpointBase<StockPriceRequest, StockPriceUpdate>
{
    protected override string Method => "POST";
    protected override string Route => "/api/stream/stock";

    protected override async Task HandleStreamAsync(
        StockPriceRequest request, 
        TypedSseStream<StockPriceUpdate> stream, 
        CancellationToken cancellationToken)
    {
        var keepAliveTask = stream.StartKeepAliveAsync(TimeSpan.FromSeconds(30), cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Type-safe event data - IntelliSense works!
                var priceUpdate = new StockPriceUpdate
                {
                    Symbol = request.Symbol,
                    Price = GetStockPrice(request.Symbol),
                    Timestamp = DateTime.UtcNow,
                    Change = Random.Shared.Next(-5, 6) / 10.0m
                };

                // Strongly-typed write - compiler enforces StockPriceUpdate type
                await stream.WriteAsync("price-update", priceUpdate, cancellationToken);

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        finally
        {
            await keepAliveTask;
        }
    }

    private static decimal GetStockPrice(string symbol)
    {
        // Simulate stock price
        return Random.Shared.Next(100, 200) + (decimal)Random.Shared.NextDouble();
    }
}

public class StockPriceRequest
{
    public string Symbol { get; set; } = string.Empty;
}

public class StockPriceUpdate
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Change { get; set; }
    public DateTime Timestamp { get; set; }
}
