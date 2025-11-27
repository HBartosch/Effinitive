# EffinitiveFramework Authentication Sample

This sample demonstrates the authentication and authorization features of EffinitiveFramework.

## Features Demonstrated

- ✅ **JWT Authentication** - Bearer token validation with roles
- ✅ **Anonymous Endpoints** - `[AllowAnonymous]` attribute
- ✅ **Protected Endpoints** - `[Authorize]` attribute
- ✅ **Role-Based Authorization** - `[Authorize(Roles = "Admin,SuperAdmin")]`
- ✅ **User Claims** - Access user information in endpoints via `Request.User`

## Running the Sample

```bash
cd samples/EffinitiveFramework.Auth.Sample
dotnet run
```

Server will start on `http://localhost:5000`

## Endpoints

### 1. Public Endpoint (No Auth)
```powershell
Invoke-RestMethod http://localhost:5000/public
```

### 2. Generate JWT Token
```powershell
# Admin user
$token = (Invoke-RestMethod -Method Post -Uri http://localhost:5000/auth/token `
  -Body (@{username='admin';password='admin123'} | ConvertTo-Json) `
  -ContentType 'application/json').token

# Regular user  
$userToken = (Invoke-RestMethod -Method Post -Uri http://localhost:5000/auth/token `
  -Body (@{username='user';password='user123'} | ConvertTo-Json) `
  -ContentType 'application/json').token
```

### 3. Access Protected Endpoint
```powershell
Invoke-RestMethod http://localhost:5000/protected `
  -Headers @{Authorization="Bearer $token"}
```

### 4. Access Admin Endpoint (Admin Role Required)
```powershell
# Works with admin token
Invoke-RestMethod http://localhost:5000/admin `
  -Headers @{Authorization="Bearer $token"}

# Fails with user token (403 Forbidden or 401 Unauthorized)
Invoke-RestMethod http://localhost:5000/admin `
  -Headers @{Authorization="Bearer $userToken"}
```

### 5. Get User Info
```powershell
Invoke-RestMethod http://localhost:5000/me `
  -Headers @{Authorization="Bearer $token"}
```

## Demo Credentials

| Username | Password  | Role  |
|----------|-----------|-------|
| admin    | admin123  | Admin |
| user     | user123   | User  |

## Architecture

### Anonymous by Default
Endpoints are accessible without authentication unless marked with `[Authorize]`:

```csharp
// Public - anyone can access
[AllowAnonymous]
public class PublicEndpoint : AsyncEndpointBase<EmptyRequest, object> { }

// Protected - requires authentication
[Authorize]
public class ProtectedEndpoint : AsyncEndpointBase<EmptyRequest, object> { }

// Admin only - requires Admin or SuperAdmin role
[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminEndpoint : AsyncEndpointBase<EmptyRequest, object> { }
```

### Accessing User Information

```csharp
public override ValueTask<object> HandleAsync(EmptyRequest request, CancellationToken ct)
{
    var user = Request.User;
    
    if (user?.IsAuthenticated == true)
    {
        var name = user.Name;
        var roles = user.FindAll(Claim.Types.Role);
        var isAdmin = user.IsInRole("Admin");
    }
    
    // ...
}
```

## JWT Configuration

JWT authentication is configured in `Program.cs`:

```csharp
.UseJwtAuthentication(options =>
{
    options.SecretKey = "your-secret-key-min-32-chars";
    options.ValidIssuer = "YourApp";
    options.ValidAudience = "YourAPI";
    options.ValidateIssuer = true;
    options.ValidateAudience = true;
    options.ValidateLifetime = true;
}, requireByDefault: false) // false = anonymous by default
```

## Performance

Even with full authentication and authorization middleware, EffinitiveFramework maintains exceptional performance - approximately **7-9x faster** than ASP.NET Core with equivalent functionality.
