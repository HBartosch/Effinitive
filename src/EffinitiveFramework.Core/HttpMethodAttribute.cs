namespace EffinitiveFramework.Core;

/// <summary>
/// Base attribute for HTTP method decorators
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public abstract class HttpMethodAttribute : Attribute
{
    public string Method { get; }
    public string Route { get; }

    protected HttpMethodAttribute(string method, string route)
    {
        Method = method;
        Route = route;
    }
}

/// <summary>
/// Marks an endpoint as handling GET requests
/// </summary>
public sealed class HttpGetAttribute : HttpMethodAttribute
{
    public HttpGetAttribute(string route) : base("GET", route) { }
}

/// <summary>
/// Marks an endpoint as handling POST requests
/// </summary>
public sealed class HttpPostAttribute : HttpMethodAttribute
{
    public HttpPostAttribute(string route) : base("POST", route) { }
}

/// <summary>
/// Marks an endpoint as handling PUT requests
/// </summary>
public sealed class HttpPutAttribute : HttpMethodAttribute
{
    public HttpPutAttribute(string route) : base("PUT", route) { }
}

/// <summary>
/// Marks an endpoint as handling DELETE requests
/// </summary>
public sealed class HttpDeleteAttribute : HttpMethodAttribute
{
    public HttpDeleteAttribute(string route) : base("DELETE", route) { }
}

/// <summary>
/// Marks an endpoint as handling PATCH requests
/// </summary>
public sealed class HttpPatchAttribute : HttpMethodAttribute
{
    public HttpPatchAttribute(string route) : base("PATCH", route) { }
}
