using EffinitiveFramework.Core;

var app = EffinitiveApp.Create()
    .UsePort(5000)
    
    // Enable automatic validation for all requests
    .UseValidation()
    
    .ConfigureJson(json =>
    {
        json.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    })
    
    .MapEndpoints(typeof(Program).Assembly)
    .Build();

Console.WriteLine("EffinitiveFramework Validation Sample");
Console.WriteLine("=====================================");
Console.WriteLine("Server running on http://localhost:5000");
Console.WriteLine();
Console.WriteLine("Endpoints:");
Console.WriteLine("  POST /users  - Create user with validation");
Console.WriteLine("  POST /orders - Create order with advanced validation");
Console.WriteLine();
Console.WriteLine("Try these commands:");
Console.WriteLine();
Console.WriteLine("# Valid user creation:");
Console.WriteLine(@"Invoke-RestMethod -Method Post -Uri http://localhost:5000/users -Body (@{name='John Doe';email='john@example.com';age=25;role='User';password='password123';confirmPassword='password123'} | ConvertTo-Json) -ContentType 'application/json'");
Console.WriteLine();
Console.WriteLine("# Invalid user (missing fields):");
Console.WriteLine(@"Invoke-RestMethod -Method Post -Uri http://localhost:5000/users -Body (@{name='J';email='invalid';age=15} | ConvertTo-Json) -ContentType 'application/json'");
Console.WriteLine();
Console.WriteLine("# Valid order:");
Console.WriteLine(@"Invoke-RestMethod -Method Post -Uri http://localhost:5000/orders -Body (@{productName='Widget';quantity=5;unitPrice=10.50;minimumOrderValue=50;totalAmount=52.50;shippingAddresses=@('123 Main St')} | ConvertTo-Json) -ContentType 'application/json'");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop");

await app.RunAsync();
