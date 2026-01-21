// Production-optimized configuration for stress testing and benchmarks
// This configuration disables debug logging and tunes for maximum throughput

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var app = EffinitiveFramework.Core.EffinitiveApp
    .Create()
    .UsePort(5000)
    .Configure(options =>
    {
        // Disable debug logging for production performance
        options.EnableDebugLogging = false;
        
        // Increase concurrent connections for stress tests
        options.MaxConcurrentConnections = Environment.ProcessorCount * 200;
        
        // Reduce timeouts for faster failure detection
        options.HeaderTimeout = TimeSpan.FromSeconds(10);
        options.RequestTimeout = TimeSpan.FromSeconds(10);
        options.IdleTimeout = TimeSpan.FromSeconds(60);
    })
    .MapEndpoints()
    .Build();

Console.WriteLine("🚀 EffinitiveFramework - Production Mode");
Console.WriteLine("========================================");
Console.WriteLine($"HTTP: http://localhost:5000");
Console.WriteLine($"Max Connections: {Environment.ProcessorCount * 200}");
Console.WriteLine($"Debug Logging: Disabled");
Console.WriteLine($"ThreadPool Min: {Environment.ProcessorCount * 2}");
Console.WriteLine();

await app.RunAsync(cts.Token);
