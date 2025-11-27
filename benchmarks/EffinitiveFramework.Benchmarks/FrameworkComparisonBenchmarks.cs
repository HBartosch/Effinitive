using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FastEndpoints;
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Layouting;
using GenHTTP.Modules.Webservices;

namespace EffinitiveFramework.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class FrameworkComparisonBenchmarks
{
    private const int EffinitivePort = 5000;
    private const int GenHttpPort = 5001;
    private const int FastEndpointsPort = 5002;
    private const int AspNetCorePort = 5003;

    private HttpClient _httpClient = null!;
    private Task _effinitiveServerTask = null!;
    private CancellationTokenSource _effinitiveCts = null!;
    private Task _genHttpServerTask = null!;
    private CancellationTokenSource _genHttpCts = null!;
    private WebApplication _fastEndpointsApp = null!;
    private Task _fastEndpointsTask = null!;
    private WebApplication _aspNetCoreApp = null!;
    private Task _aspNetCoreTask = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Start EffinitiveFramework server
        _effinitiveCts = new CancellationTokenSource();
        var effinitiveApp = EffinitiveFramework.Core.EffinitiveApp.Create()
            .UsePort(EffinitivePort)
            .MapEndpoints(typeof(BenchmarkEndpoint).Assembly)
            .Build();
        
        _effinitiveServerTask = Task.Run(async () =>
        {
            try
            {
                await effinitiveApp.RunAsync(_effinitiveCts.Token);
            }
            catch (OperationCanceledException) { }
        });

        // Start GenHTTP server
        _genHttpCts = new CancellationTokenSource();
        _genHttpServerTask = Task.Run(async () =>
        {
            try
            {
                var layout = Layout.Create().AddService<GenHttpBenchmarkService>("api");
                var host = GenHTTP.Engine.Internal.Host.Create()
                    .Handler(layout)
                    .Port((ushort)GenHttpPort);
                
                await host.StartAsync();
            }
            catch { }
        });


        // Start FastEndpoints server
        var feBuilder = WebApplication.CreateBuilder();
        feBuilder.WebHost.UseUrls($"http://localhost:{FastEndpointsPort}");
        feBuilder.Services.AddFastEndpoints();
        
        _fastEndpointsApp = feBuilder.Build();
        _fastEndpointsApp.UseFastEndpoints();
        
        _fastEndpointsTask = _fastEndpointsApp.RunAsync();

        // Start ASP.NET Core Minimal API server (baseline comparison)
        var aspBuilder = WebApplication.CreateBuilder();
        aspBuilder.WebHost.UseUrls($"http://localhost:{AspNetCorePort}");
        
        _aspNetCoreApp = aspBuilder.Build();
        
        _aspNetCoreApp.MapGet("/api/benchmark", async (HttpContext context) =>
        {
            var response = new BenchmarkResponse
            {
                Message = "Pong",
                Timestamp = DateTime.UtcNow
            };
            await context.Response.WriteAsJsonAsync(response);
        });
        
        _aspNetCoreApp.MapPost("/api/benchmark", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BenchmarkRequest>();
            var response = new BenchmarkResponse
            {
                Message = request?.Message ?? "Pong",
                Timestamp = DateTime.UtcNow
            };
            await context.Response.WriteAsJsonAsync(response);
        });
        
        _aspNetCoreTask = _aspNetCoreApp.RunAsync();

        // Wait for servers to start
        await Task.Delay(2000);

        // Warm up
        await WarmupServer($"http://localhost:{EffinitivePort}/api/benchmark");
        await WarmupServer($"http://localhost:{GenHttpPort}/api/benchmark");
        await WarmupServer($"http://localhost:{FastEndpointsPort}/api/benchmark");
        await WarmupServer($"http://localhost:{AspNetCorePort}/api/benchmark");
    }

    private async Task WarmupServer(string url)
    {
        try
        {
            for (int i = 0; i < 10; i++)
            {
                await _httpClient.GetAsync(url);
            }
        }
        catch { }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _httpClient?.Dispose();
        
        _effinitiveCts?.Cancel();
        if (_effinitiveServerTask != null)
        {
            try
            {
                await _effinitiveServerTask;
            }
            catch { }
        }

        _genHttpCts?.Cancel();
        if (_genHttpServerTask != null)
        {
            try
            {
                await Task.WhenAny(_genHttpServerTask, Task.Delay(1000));
            }
            catch { }
        }

        if (_fastEndpointsApp != null)
        {
            await _fastEndpointsApp.StopAsync();
            await _fastEndpointsApp.DisposeAsync();
        }

        if (_aspNetCoreApp != null)
        {
            await _aspNetCoreApp.StopAsync();
            await _aspNetCoreApp.DisposeAsync();
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<string> GetRequest_EffinitiveFramework()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{EffinitivePort}/api/benchmark");
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> GetRequest_GenHTTP()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{GenHttpPort}/api/benchmark");
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> GetRequest_FastEndpoints()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{FastEndpointsPort}/api/benchmark");
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> GetRequest_AspNetCoreMinimal()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{AspNetCorePort}/api/benchmark");
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> PostRequest_EffinitiveFramework()
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new BenchmarkRequest { Message = "Test" }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync($"http://localhost:{EffinitivePort}/api/benchmark", content);
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> PostRequest_GenHTTP()
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new BenchmarkRequest { Message = "Test" }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync($"http://localhost:{GenHttpPort}/api/benchmark", content);
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> PostRequest_FastEndpoints()
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new BenchmarkRequest { Message = "Test" }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync($"http://localhost:{FastEndpointsPort}/api/benchmark", content);
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> PostRequest_AspNetCoreMinimal()
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new BenchmarkRequest { Message = "Test" }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync($"http://localhost:{AspNetCorePort}/api/benchmark", content);
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> GetRequest_EffinitiveFramework_Async()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{EffinitivePort}/api/benchmark-async");
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task<string> PostRequest_EffinitiveFramework_Async()
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new BenchmarkRequest { Message = "Test" }),
            Encoding.UTF8,
            "application/json");
        
        var response = await _httpClient.PostAsync($"http://localhost:{EffinitivePort}/api/benchmark-async", content);
        return await response.Content.ReadAsStringAsync();
    }
}

// EffinitiveFramework endpoint (sync version using ValueTask)
public class BenchmarkEndpoint : EffinitiveFramework.Core.EndpointBase<BenchmarkRequest, BenchmarkResponse>
{
    protected override string Method => "GET,POST";
    protected override string Route => "/api/benchmark";

    public override ValueTask<BenchmarkResponse> HandleAsync(BenchmarkRequest request, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new BenchmarkResponse
        {
            Message = request.Message ?? "Pong",
            Timestamp = DateTime.UtcNow
        });
    }
}

// EffinitiveFramework async endpoint (using AsyncEndpointBase with Task)
public class AsyncBenchmarkEndpoint : EffinitiveFramework.Core.AsyncEndpointBase<BenchmarkRequest, BenchmarkResponse>
{
    protected override string Method => "GET,POST";
    protected override string Route => "/api/benchmark-async";

    public override async Task<BenchmarkResponse> HandleAsync(BenchmarkRequest request, CancellationToken cancellationToken = default)
    {
        // Simulate minimal async work to test async endpoint performance
        await Task.Yield();
        
        return new BenchmarkResponse
        {
            Message = request.Message ?? "Pong",
            Timestamp = DateTime.UtcNow
        };
    }
}

// FastEndpoints endpoint
public class FastEndpointsBenchmark : FastEndpoints.Endpoint<FastEndpoints.EmptyRequest>
{
    public override void Configure()
    {
        Verbs(Http.GET, Http.POST);
        Routes("/api/benchmark");
        AllowAnonymous();
    }

    public override async Task HandleAsync(FastEndpoints.EmptyRequest req, CancellationToken ct)
    {
        var response = new BenchmarkResponse
        {
            Message = "Pong",
            Timestamp = DateTime.UtcNow
        };
        await HttpContext.Response.WriteAsJsonAsync(response, ct);
    }
}

// GenHTTP service
public class GenHttpBenchmarkService
{
    public BenchmarkResponse Benchmark()
    {
        return new BenchmarkResponse
        {
            Message = "Pong",
            Timestamp = DateTime.UtcNow
        };
    }
}

// Shared DTOs
public class BenchmarkRequest
{
    public string? Message { get; set; }
}

public class BenchmarkResponse
{
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
