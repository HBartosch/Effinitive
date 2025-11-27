using EffinitiveFramework.Core;
using EffinitiveFramework.Core.DependencyInjection;
using EffinitiveFramework.EFCore.Sample.Data;
using EffinitiveFramework.EFCore.Sample.Services;
using Microsoft.EntityFrameworkCore;

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   EffinitiveFramework + Entity Framework Core Sample    ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

var app = EffinitiveApp.Create()
    .ConfigureServices(services =>
    {
        // Database context (scoped - one per request)
        services.AddScoped<AppDbContext>(sp =>
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=products.db")
                .Options;
            return new AppDbContext(options);
        });

        // Business services
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IOrderService, OrderService>();
    })
    .UsePort(5000)
    .ConfigureJson(json =>
    {
        json.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        json.WriteIndented = true;
    })
    .MapEndpoints(typeof(Program).Assembly)
    .Build();

// Initialize database
Console.WriteLine("🔧 Initializing database...");
using (var scope = ((EffinitiveFramework.Core.DependencyInjection.ServiceProvider)app.Services!).CreateScope())
{
    var context = scope.ServiceProvider.GetService<AppDbContext>();
    if (context != null)
    {
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        Console.WriteLine("✅ Database initialized with seed data");
    }
}

Console.WriteLine();
Console.WriteLine("🚀 Server starting...");
Console.WriteLine("📡 HTTP: http://localhost:5000");
Console.WriteLine();
Console.WriteLine("Available endpoints:");
Console.WriteLine("  Products:");
Console.WriteLine("    GET    /api/products                    - Get all products");
Console.WriteLine("    GET    /api/products/{id}               - Get product by ID");
Console.WriteLine("    GET    /api/products/category/{category} - Get products by category");
Console.WriteLine("    POST   /api/products                    - Create product");
Console.WriteLine("    PUT    /api/products/{id}               - Update product");
Console.WriteLine("    DELETE /api/products/{id}               - Delete product");
Console.WriteLine();
Console.WriteLine("  Orders:");
Console.WriteLine("    GET    /api/orders                      - Get all orders");
Console.WriteLine("    GET    /api/orders/{id}                 - Get order by ID");
Console.WriteLine("    GET    /api/orders/customer/{email}     - Get orders by customer");
Console.WriteLine("    POST   /api/orders                      - Create order");
Console.WriteLine("    PATCH  /api/orders/{id}/status          - Update order status");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop");
Console.WriteLine();

await app.RunAsync();
