using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authentication;
using EffinitiveFramework.Core.Authorization;

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   EffinitiveFramework Authentication Demo               ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Example 1: JWT Authentication
Console.WriteLine("Starting server with JWT authentication...");
Console.WriteLine("  - Public endpoints: No authentication required");
Console.WriteLine("  - Protected endpoints: Require valid JWT token");
Console.WriteLine("  - Admin endpoints: Require 'Admin' role");
Console.WriteLine();

var app = EffinitiveApp.Create()
    .UseJwtAuthentication(options =>
    {
        options.SecretKey = "my-super-secret-key-that-is-at-least-32-characters-long!";
        options.ValidIssuer = "EffinitiveFramework";
        options.ValidAudience = "EffinitiveFrameworkAPI";
        options.ValidateIssuer = true;
        options.ValidateAudience = true;
        options.ValidateLifetime = true;
    }, requireByDefault: false) // Anonymous by default, require [Authorize] attribute
    .UsePort(5000)
    .ConfigureJson(json =>
    {
        json.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        json.WriteIndented = true;
    })
    .MapEndpoints(typeof(Program).Assembly)
    .Build();

Console.WriteLine("🚀 Server starting...");
Console.WriteLine("📡 HTTP: http://localhost:5000");
Console.WriteLine();
Console.WriteLine("Available endpoints:");
Console.WriteLine("  🌐 GET  /public                  - Public endpoint (no auth)");
Console.WriteLine("  🔒 GET  /protected               - Protected endpoint (requires JWT)");
Console.WriteLine("  👑 GET  /admin                   - Admin endpoint (requires Admin role)");
Console.WriteLine("  👤 GET  /me                      - Get user info from token");
Console.WriteLine("  🎫 POST /auth/token              - Generate JWT token");
Console.WriteLine();
Console.WriteLine("Test commands:");
Console.WriteLine("  # Get public data (no token needed)");
Console.WriteLine("  Invoke-RestMethod http://localhost:5000/public");
Console.WriteLine();
Console.WriteLine("  # Get a JWT token");
Console.WriteLine("  $token = (Invoke-RestMethod -Method Post -Uri http://localhost:5000/auth/token -Body (@{username='admin';password='admin123'} | ConvertTo-Json) -ContentType 'application/json').token");
Console.WriteLine();
Console.WriteLine("  # Access protected endpoint");
Console.WriteLine("  Invoke-RestMethod http://localhost:5000/protected -Headers @{Authorization=\"Bearer $token\"}");
Console.WriteLine();
Console.WriteLine("  # Access admin endpoint");
Console.WriteLine("  Invoke-RestMethod http://localhost:5000/admin -Headers @{Authorization=\"Bearer $token\"}");
Console.WriteLine();
Console.WriteLine("  # Get user info");
Console.WriteLine("  Invoke-RestMethod http://localhost:5000/me -Headers @{Authorization=\"Bearer $token\"}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop");
Console.WriteLine();

await app.RunAsync();
