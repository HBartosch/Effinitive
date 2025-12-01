using EffinitiveFramework.Core;

namespace EffinitiveFramework.Sample.Endpoints;

/// <summary>
/// FastEndpoints-style implementation: GET / returns empty string
/// Showcases minimal, clean endpoint syntax similar to FastEndpoints
/// </summary>
public class HomeEndpoint : NoRequestEndpointBase<string>
{
    protected override string Method => "GET";
    protected override string Route => "/";
    protected override string ContentType => "text/plain";

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(string.Empty);
    }
}

/// <summary>
/// FastEndpoints-style implementation: GET /user/{id} returns the id
/// </summary>
public class UserByIdEndpoint : NoRequestEndpointBase<string>
{
    protected override string Method => "GET";
    protected override string Route => "/user/{id}";
    protected override string ContentType => "text/plain";

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        // ASP.NET Core-style route value access
        var id = HttpContext?.RouteValues?["id"] ?? string.Empty;
        return ValueTask.FromResult(id);
    }
}

/// <summary>
/// FastEndpoints-style implementation: POST /user returns empty string
/// </summary>
public class UserEndpoint : NoRequestEndpointBase<string>
{
    protected override string Method => "POST";
    protected override string Route => "/user";
    protected override string ContentType => "text/plain";

    public override ValueTask<string> HandleAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(string.Empty);
    }
}
