using EffinitiveFramework.Core;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// Example of ValueTask-based endpoint for in-memory/cached operations
/// </summary>
public class GetUsersEndpoint : EndpointBase<EmptyRequest, UsersResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/users";

    public override ValueTask<UsersResponse> HandleAsync(EmptyRequest request, CancellationToken cancellationToken = default)
    {
        // This is in-memory data, so ValueTask is perfect (no allocation if completed synchronously)
        var users = new List<User>
        {
            new User { Id = 1, Name = "Alice", Email = "alice@example.com" },
            new User { Id = 2, Name = "Bob", Email = "bob@example.com" },
            new User { Id = 3, Name = "Charlie", Email = "charlie@example.com" }
        };

        return ValueTask.FromResult(new UsersResponse { Users = users, Total = users.Count });
    }
}

/// <summary>
/// Example of Task-based endpoint for I/O operations (database, external APIs, etc.)
/// </summary>
public class CreateUserEndpoint : AsyncEndpointBase<CreateUserRequest, UserResponse>
{
    protected override string Method => "POST";
    protected override string Route => "/api/users";

    public override async Task<UserResponse> HandleAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        // Simulate async I/O operation (database insert, external API call, etc.)
        await Task.Delay(10, cancellationToken); // Simulates DB operation
        
        var user = new User
        {
            Id = Random.Shared.Next(1000, 9999),
            Name = request.Name,
            Email = request.Email
        };

        return new UserResponse { User = user, Success = true, Message = "User created successfully" };
    }
}

/// <summary>
/// Example of ValueTask-based endpoint for simple synchronous operations
/// </summary>
public class GetHealthEndpoint : EndpointBase<EmptyRequest, HealthResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/health";

    public override ValueTask<HealthResponse> HandleAsync(EmptyRequest request, CancellationToken cancellationToken = default)
    {
        // Pure synchronous operation - ValueTask avoids allocation
        return ValueTask.FromResult(new HealthResponse 
        { 
            Status = "Healthy",
            Timestamp = DateTime.UtcNow,
            Version = "1.0.0"
        });
    }
}

/// <summary>
/// Example of Task-based endpoint simulating a database query
/// </summary>
public class GetUserByIdEndpoint : AsyncEndpointBase<UserByIdRequest, UserResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/users/search";

    public override async Task<UserResponse> HandleAsync(UserByIdRequest request, CancellationToken cancellationToken = default)
    {
        // Simulate database query with async I/O
        await Task.Delay(5, cancellationToken); // Simulates DB query
        
        var user = new User
        {
            Id = request.Id,
            Name = "Database User",
            Email = $"user{request.Id}@example.com"
        };

        return new UserResponse 
        { 
            User = user, 
            Success = true, 
            Message = "User retrieved from database" 
        };
    }
}

// Request/Response DTOs
public record CreateUserRequest
{
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

public record UserByIdRequest
{
    public int Id { get; init; }
}

public record User
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

public record UsersResponse
{
    public List<User> Users { get; init; } = new();
    public int Total { get; init; }
}

public record UserResponse
{
    public User? User { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public record HealthResponse
{
    public string Status { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Version { get; init; } = string.Empty;
}
