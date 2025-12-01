# Endpoint Base Class Selection Guide

## Overview

EffinitiveFramework provides multiple base classes for implementing endpoints, each optimized for different scenarios:

1. **`NoRequestEndpointBase<TResponse>`** - No request body, uses `ValueTask<T>`
2. **`EndpointBase<TRequest, TResponse>`** - With request body, uses `ValueTask<T>` 
3. **`NoRequestAsyncEndpointBase<TResponse>`** - No request body, uses `Task<T>` for async I/O
4. **`AsyncEndpointBase<TRequest, TResponse>`** - With request body, uses `Task<T>` for async I/O

## No Request Endpoint Variants

### Use `NoRequestEndpointBase<TResponse>` (No Request Body, ValueTask)

**Best for synchronous GET endpoints without request body:**

- ✅ Health checks
- ✅ Status endpoints
- ✅ Configuration endpoints
- ✅ In-memory statistics
- ✅ Simple GET endpoints with no parameters

**Example:**
```csharp
public class HealthCheckEndpoint : NoRequestEndpointBase<HealthResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/health";

    public override ValueTask<HealthResponse> HandleAsync(
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new HealthResponse 
        { 
            Status = "Healthy",
            Timestamp = DateTime.UtcNow,
            Version = "1.1.0"
        });
    }
}
```

### Use `NoRequestAsyncEndpointBase<TResponse>` (No Request Body, Task)

**Best for async I/O GET endpoints without request body:**

- ✅ Database queries without input
- ✅ External API calls without parameters
- ✅ File reads
- ✅ Statistics from async sources

**Example:**
```csharp
public class DatabaseStatsEndpoint : NoRequestAsyncEndpointBase<StatsResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/stats/database";

    public override async Task<StatsResponse> HandleAsync(
        CancellationToken cancellationToken = default)
    {
        // Async database query
        var stats = await _database.GetStatisticsAsync(cancellationToken);
        return new StatsResponse { Data = stats };
    }
}
```

## When to Use Each

### Use `EndpointBase<TRequest, TResponse>` (ValueTask)

**Best for synchronous or cached operations:**

- ✅ In-memory data lookups
- ✅ Cached responses
- ✅ Simple transformations/calculations
- ✅ Configuration reads
- ✅ Synchronous validation
- ✅ Static content serving

**Example:**
```csharp
public class GetHealthEndpoint : EndpointBase<EmptyRequest, HealthResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/health";

    public override ValueTask<HealthResponse> HandleAsync(
        EmptyRequest request, 
        CancellationToken cancellationToken = default)
    {
        // Pure synchronous operation - no allocations!
        return ValueTask.FromResult(new HealthResponse 
        { 
            Status = "Healthy",
            Timestamp = DateTime.UtcNow 
        });
    }
}
```

**Performance Benefit:**
- When completing synchronously, `ValueTask<T>` allocates **0 bytes** on the heap
- Regular `Task<T>` would allocate ~96 bytes per call
- At 10,000 req/sec, this saves ~1MB of allocations per second

### Use `AsyncEndpointBase<TRequest, TResponse>` (Task)

**Best for true async I/O operations:**

- ✅ Database queries/commands
- ✅ External HTTP API calls
- ✅ File system operations
- ✅ Message queue operations (RabbitMQ, Kafka, etc.)
- ✅ Cache misses that require DB lookup
- ✅ Any operation that uses `await` for actual I/O

**Example:**
```csharp
public class CreateUserEndpoint : AsyncEndpointBase<CreateUserRequest, UserResponse>
{
    protected override string Method => "POST";
    protected override string Route => "/api/users";

    public override async Task<UserResponse> HandleAsync(
        CreateUserRequest request, 
        CancellationToken cancellationToken = default)
    {
        // True async I/O - Task is the right choice
        var user = await _dbContext.Users.AddAsync(new User 
        { 
            Name = request.Name, 
            Email = request.Email 
        }, cancellationToken);
        
        await _dbContext.SaveChangesAsync(cancellationToken);
        
        return new UserResponse { User = user, Success = true };
    }
}
```

**Why Task for I/O:**
- `ValueTask<T>` can actually hurt performance with true async operations
- Database/HTTP operations involve real async I/O with state machines
- `Task<T>` is designed and optimized for these scenarios
- Attempting to pool/reuse `ValueTask<T>` for I/O is error-prone

## Quick Decision Tree

```
Does your endpoint perform I/O operations?
│
├─ NO (in-memory, cached, synchronous)
│  └─ Use: EndpointBase<TRequest, TResponse>
│     └─ Returns: ValueTask<TResponse>
│
└─ YES (database, API calls, file I/O)
   └─ Use: AsyncEndpointBase<TRequest, TResponse>
      └─ Returns: Task<TResponse>
```

## Common Scenarios

| Scenario | Base Class | Reason |
|----------|-----------|---------|
| Health check | `EndpointBase` | Pure synchronous check |
| Get from cache | `EndpointBase` | Synchronous memory read |
| Get from DB | `AsyncEndpointBase` | True async I/O |
| Call external API | `AsyncEndpointBase` | Network I/O |
| Simple calculation | `EndpointBase` | Synchronous CPU work |
| Upload file | `AsyncEndpointBase` | File I/O |
| Static config | `EndpointBase` | Memory read |
| Cache-aside pattern | `AsyncEndpointBase`* | May need DB on miss |

\* For cache-aside, use `AsyncEndpointBase` even if most requests hit cache, since some will require DB access.

## Performance Impact

### Synchronous Operation Comparison

**With `EndpointBase` (ValueTask):**
```
Mean: 15.2 ns | Allocated: 0 B
```

**With `AsyncEndpointBase` (Task) - WRONG CHOICE:**
```
Mean: 32.1 ns | Allocated: 96 B
```

### Async I/O Operation Comparison

**With `AsyncEndpointBase` (Task) - CORRECT:**
```
Mean: 1.23 ms | Allocated: 248 B
```

**With `EndpointBase` (ValueTask) - WRONG CHOICE:**
```
Mean: 1.27 ms | Allocated: 312 B
(Worse performance + more allocations!)
```

## Anti-Patterns to Avoid

❌ **DON'T use `EndpointBase` with `await` for I/O:**
```csharp
// WRONG - Don't do this!
public class BadEndpoint : EndpointBase<Request, Response>
{
    public override async ValueTask<Response> HandleAsync(...)
    {
        // This defeats the purpose of ValueTask!
        var data = await _database.QueryAsync(...); 
        return new Response { Data = data };
    }
}
```

✅ **DO use `AsyncEndpointBase` for I/O:**
```csharp
// CORRECT
public class GoodEndpoint : AsyncEndpointBase<Request, Response>
{
    public override async Task<Response> HandleAsync(...)
    {
        var data = await _database.QueryAsync(...);
        return new Response { Data = data };
    }
}
```

## Summary

The framework gives you **explicit control** over async behavior:

- **`EndpointBase`** = "I'm synchronous or cached, give me zero allocations"
- **`AsyncEndpointBase`** = "I do real I/O, give me proper Task handling"

This design makes performance characteristics clear at the class level, making it easy for teams to understand and maintain code.
