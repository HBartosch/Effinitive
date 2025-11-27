using EffinitiveFramework.Sample.Endpoints;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var app = EffinitiveFramework.Core.EffinitiveApp
    .Create()
    .UsePort(5000)           // HTTP on port 5000
    .UseHttpsPort(5001)      // HTTPS on port 5001 (HTTP/2 enabled via ALPN)
    .ConfigureTls(tls =>
    {
        // Use development certificate (creates one if needed)
        tls.CertificatePath = "localhost.pfx";
        tls.CertificatePassword = "dev-password";
    })
    .MapEndpoints()
    .Build();

Console.WriteLine("🚀 EffinitiveFramework Sample Server");
Console.WriteLine("=====================================");
Console.WriteLine($"HTTP/1.1:  http://localhost:5000");
Console.WriteLine($"HTTP/2:    https://localhost:5001 (ALPN: h2)");
Console.WriteLine($"Endpoints: /api/users (GET, POST)");
Console.WriteLine("Press Ctrl+C to stop");
Console.WriteLine();

await app.RunAsync(cts.Token);

