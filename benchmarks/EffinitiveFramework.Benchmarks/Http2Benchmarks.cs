using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using FastEndpoints;

namespace EffinitiveFramework.Benchmarks;

/// <summary>
/// HTTP/2 end-to-end benchmarks comparing frameworks that support HTTP/2
/// Tests over HTTPS with ALPN negotiation (h2 protocol)
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class Http2Benchmarks
{
    private HttpClient? _http2Client;
    private Core.EffinitiveApp? _effinitiveHttpsServer;
    private IHost? _fastEndpointsServer;
    private IHost? _minimalApiServer;
    private CancellationTokenSource? _effinitiveCts;
    
    private const string EffinitiveHttpsUrl = "https://localhost:6001";
    private const string FastEndpointsUrl = "https://localhost:6002";
    private const string MinimalApiUrl = "https://localhost:6003";
    
    [GlobalSetup]
    public async Task Setup()
    {
        // Create HTTP/2 client
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        
        _http2Client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        
        // Start EffinitiveFramework HTTPS server (HTTP/2 via ALPN)
        _effinitiveCts = new CancellationTokenSource();
        _effinitiveHttpsServer = StartEffinitiveHttpsServerAsync(_effinitiveCts.Token);
        
        // Start FastEndpoints HTTPS server (HTTP/2 via Kestrel)
        _fastEndpointsServer = await StartFastEndpointsServerAsync();
        
        // Start ASP.NET Core Minimal API HTTPS server (HTTP/2 via Kestrel)
        _minimalApiServer = await StartMinimalApiServerAsync();
        
        // Warmup
        await Task.Delay(2000);
        
        // Verify all servers respond with HTTP/2
        await VerifyHttp2Support();
    }
    
    [GlobalCleanup]
    public async Task Cleanup()
    {
        _http2Client?.Dispose();
        
        if (_effinitiveHttpsServer != null)
        {
            _effinitiveCts?.Cancel();
            await _effinitiveHttpsServer.StopAsync();
            _effinitiveHttpsServer.Dispose();
        }
        
        _effinitiveCts?.Dispose();
        
        if (_fastEndpointsServer != null)
            await _fastEndpointsServer.StopAsync();
        
        if (_minimalApiServer != null)
            await _minimalApiServer.StopAsync();
    }
    
    private Core.EffinitiveApp StartEffinitiveHttpsServerAsync(CancellationToken cancellationToken)
    {
        // Walk up from AppContext.BaseDirectory to find the solution root, then locate the cert.
        // BenchmarkDotNet runs from a temp artifact directory, so we can't use a fixed relative path.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EffinitiveFramework.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Could not locate solution root from " + AppContext.BaseDirectory);
        var certPath = Path.Combine(dir.FullName, "samples", "EffinitiveFramework.Sample", "localhost.pfx");
        
        var app = Core.EffinitiveApp
            .Create()
            .UsePort(0) // Disable HTTP
            .UseHttpsPort(6001)
            .ConfigureTls(tls =>
            {
                tls.CertificatePath = certPath;
                tls.CertificatePassword = "dev-password";
            })
            .MapEndpoints(typeof(Http2BenchmarkEndpoint).Assembly)
            .Build();
        
        _ = Task.Run(async () => await app.RunAsync(cancellationToken), cancellationToken);
        Thread.Sleep(1000); // Give server time to start
        
        return app;
    }
    
    private async Task<IHost> StartFastEndpointsServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(6002, listenOptions =>
            {
                listenOptions.UseHttps();
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });
        
        // Add FastEndpoints services
        builder.Services.AddFastEndpoints();
        
        var app = builder.Build();
        app.UseFastEndpoints();
        
        await app.StartAsync();
        return app;
    }
    
    private async Task<IHost> StartMinimalApiServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(6003, listenOptions =>
            {
                listenOptions.UseHttps();
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });
        
        var app = builder.Build();
        
        app.MapGet("/api/http2-benchmark", () => new Http2BenchmarkResponse
        {
            Message = "HTTP/2 GET Response",
            Timestamp = DateTime.UtcNow
        });
        
        app.MapPost("/api/http2-benchmark", (Http2BenchmarkRequest request) => new Http2BenchmarkResponse
        {
            Message = $"HTTP/2 POST Response: {request?.Name ?? "Unknown"}",
            Timestamp = DateTime.UtcNow
        });
        
        await app.StartAsync();
        return app;
    }
    
    private async Task VerifyHttp2Support()
    {
        Console.WriteLine("\n=== Verifying HTTP/2 Support ===");
        
        try
        {
            var response = await _http2Client!.GetAsync($"{EffinitiveHttpsUrl}/api/http2-benchmark");
            Console.WriteLine($"EffinitiveFramework: {response.Version} (expected 2.0)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EffinitiveFramework: ERROR - {ex.Message}");
        }
        
        try
        {
            var response = await _http2Client!.GetAsync($"{FastEndpointsUrl}/api/http2-benchmark");
            Console.WriteLine($"FastEndpoints: {response.Version}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FastEndpoints: ERROR - {ex.Message}");
        }
        
        try
        {
            var response = await _http2Client!.GetAsync($"{MinimalApiUrl}/api/http2-benchmark");
            Console.WriteLine($"ASP.NET Minimal: {response.Version}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ASP.NET Minimal: ERROR - {ex.Message}");
        }
        
        Console.WriteLine("================================\n");
    }
    
    // ==================== GET Benchmarks ====================
    
    [Benchmark(Description = "GET - EffinitiveFramework (HTTP/2)")]
    public async Task<Http2BenchmarkResponse?> GetEffinitive()
    {
        var response = await _http2Client!.GetAsync($"{EffinitiveHttpsUrl}/api/http2-benchmark");
        return await response.Content.ReadFromJsonAsync<Http2BenchmarkResponse>();
    }
    
    [Benchmark(Description = "GET - FastEndpoints (HTTP/2)")]
    public async Task<Http2BenchmarkResponse?> GetFastEndpoints()
    {
        var response = await _http2Client!.GetAsync($"{FastEndpointsUrl}/api/http2-benchmark");
        return await response.Content.ReadFromJsonAsync<Http2BenchmarkResponse>();
    }
    
    [Benchmark(Description = "GET - ASP.NET Minimal (HTTP/2)")]
    public async Task<Http2BenchmarkResponse?> GetMinimalApi()
    {
        var response = await _http2Client!.GetAsync($"{MinimalApiUrl}/api/http2-benchmark");
        return await response.Content.ReadFromJsonAsync<Http2BenchmarkResponse>();
    }
    
    // ==================== POST Benchmarks ====================
    
    [Benchmark(Description = "POST - EffinitiveFramework (HTTP/2)")]
    public async Task<Http2BenchmarkResponse?> PostEffinitive()
    {
        var request = new Http2BenchmarkRequest { Name = "HTTP/2 Test", Email = "http2@test.com" };
        var content = JsonContent.Create(request);
        var response = await _http2Client!.PostAsync($"{EffinitiveHttpsUrl}/api/http2-benchmark", content);
        return await response.Content.ReadFromJsonAsync<Http2BenchmarkResponse>();
    }
    
    [Benchmark(Description = "POST - FastEndpoints (HTTP/2)")]
    public async Task<Http2BenchmarkResponse?> PostFastEndpoints()
    {
        var request = new Http2BenchmarkRequest { Name = "HTTP/2 Test", Email = "http2@test.com" };
        var content = JsonContent.Create(request);
        var response = await _http2Client!.PostAsync($"{FastEndpointsUrl}/api/http2-benchmark", content);
        return await response.Content.ReadFromJsonAsync<Http2BenchmarkResponse>();
    }
    
    [Benchmark(Description = "POST - ASP.NET Minimal (HTTP/2)")]
    public async Task<Http2BenchmarkResponse?> PostMinimalApi()
    {
        var request = new Http2BenchmarkRequest { Name = "HTTP/2 Test", Email = "http2@test.com" };
        var content = JsonContent.Create(request);
        var response = await _http2Client!.PostAsync($"{MinimalApiUrl}/api/http2-benchmark", content);
        return await response.Content.ReadFromJsonAsync<Http2BenchmarkResponse>();
    }
}

// ==================== Endpoint for EffinitiveFramework ====================

public class Http2BenchmarkEndpoint : EffinitiveFramework.Core.EndpointBase<Http2BenchmarkRequest, Http2BenchmarkResponse>
{
    protected override string Route => "/api/http2-benchmark";
    protected override string Method => "GET";

    public override ValueTask<Http2BenchmarkResponse> HandleAsync(Http2BenchmarkRequest request, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new Http2BenchmarkResponse
        {
            Message = $"HTTP/2 Response: {request.Name ?? "GET"}",
            Timestamp = DateTime.UtcNow
        });
    }
}

public class Http2BenchmarkEndpointPost : EffinitiveFramework.Core.AsyncEndpointBase<Http2BenchmarkRequest, Http2BenchmarkResponse>
{
    protected override string Route => "/api/http2-benchmark";
    protected override string Method => "POST";

    public override async Task<Http2BenchmarkResponse> HandleAsync(Http2BenchmarkRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();

        return new Http2BenchmarkResponse
        {
            Message = $"HTTP/2 Response: {request.Name ?? "POST"}",
            Timestamp = DateTime.UtcNow
        };
    }
}

// ==================== FastEndpoints Endpoints ====================

public class FastEndpointsHttp2GetEndpoint : Endpoint<Http2BenchmarkRequest, Http2BenchmarkResponse>
{
    public override void Configure()
    {
        Get("/api/http2-benchmark");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Http2BenchmarkRequest req, CancellationToken ct)
    {
        var response = new Http2BenchmarkResponse
        {
            Message = $"HTTP/2 FastEndpoints GET: {req.Name ?? "GET"}",
            Timestamp = DateTime.UtcNow
        };
        await HttpContext.Response.WriteAsJsonAsync(response, ct);
    }
}

public class FastEndpointsHttp2PostEndpoint : Endpoint<Http2BenchmarkRequest, Http2BenchmarkResponse>
{
    public override void Configure()
    {
        Post("/api/http2-benchmark");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Http2BenchmarkRequest req, CancellationToken ct)
    {
        var response = new Http2BenchmarkResponse
        {
            Message = $"HTTP/2 FastEndpoints POST: {req.Name ?? "POST"}",
            Timestamp = DateTime.UtcNow
        };
        await HttpContext.Response.WriteAsJsonAsync(response, ct);
    }
}

// ==================== DTOs ====================

public class Http2BenchmarkRequest
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

public class Http2BenchmarkResponse
{
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
