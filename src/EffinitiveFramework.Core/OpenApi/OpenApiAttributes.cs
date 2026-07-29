namespace EffinitiveFramework.Core.OpenApi;

/// <summary>
/// Describes an endpoint in the generated OpenAPI document. Everything here is optional — an endpoint
/// without this attribute is still documented, just with a generated operation id and no prose.
/// </summary>
/// <example>
/// <code>
/// [OpenApiOperation(Summary = "List products", Tags = "Catalog")]
/// public class GetProductsEndpoint : NoRequestAsyncEndpointBase&lt;Product[]&gt; { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class OpenApiOperationAttribute : Attribute
{
    /// <summary>One-line summary shown next to the operation in UI tools.</summary>
    public string? Summary { get; set; }

    /// <summary>Longer description. Rendered as Markdown by most viewers.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Comma-separated tags used to group operations, e.g. "Catalog, Admin".
    /// Defaults to the first path segment when omitted.
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// Unique operation id used by client generators. Defaults to the endpoint type name with any
    /// "Endpoint" suffix trimmed.
    /// </summary>
    public string? OperationId { get; set; }

    /// <summary>Marks the operation as deprecated.</summary>
    public bool Deprecated { get; set; }
}

/// <summary>
/// Documents a response the endpoint can produce beyond the success response inferred from its
/// TResponse type. Repeatable — one per status code.
/// </summary>
/// <example>
/// <code>
/// [OpenApiResponse(404, Description = "No product with that id")]
/// [OpenApiResponse(409, typeof(ConflictDetails), Description = "SKU already exists")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class OpenApiResponseAttribute : Attribute
{
    /// <summary>HTTP status code this response describes.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// Response payload type. Null means the framework's <c>ProblemDetails</c> for 4xx/5xx, or no
    /// body at all for 204.
    /// </summary>
    public Type? Type { get; }

    /// <summary>Human-readable description. Defaults to the standard reason phrase.</summary>
    public string? Description { get; set; }

    /// <summary>Content type. Defaults to application/json, or application/problem+json for 4xx/5xx.</summary>
    public string? ContentType { get; set; }

    public OpenApiResponseAttribute(int statusCode)
    {
        StatusCode = statusCode;
    }

    public OpenApiResponseAttribute(int statusCode, Type type)
    {
        StatusCode = statusCode;
        Type = type;
    }
}

/// <summary>Where a parameter is carried.</summary>
public enum OpenApiParameterLocation
{
    Query = 0,
    Header = 1,
    Path = 2,
    Cookie = 3
}

/// <summary>
/// Documents a parameter the framework cannot infer.
/// <para>
/// Route parameters are detected automatically from <c>{placeholders}</c> in the pattern and typed from
/// the request object, so this attribute is mainly for query strings and headers: endpoints read those
/// through <c>HttpContext.Query</c> and <c>HttpContext.Headers</c> imperatively, which leaves nothing
/// for the generator to reflect over.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [OpenApiParameter("page", Type = typeof(int), Description = "1-based page number")]
/// [OpenApiParameter("X-Tenant", In = OpenApiParameterLocation.Header, Required = true)]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class OpenApiParameterAttribute : Attribute
{
    /// <summary>Parameter name as it appears on the wire.</summary>
    public string Name { get; }

    /// <summary>Where the parameter is carried. Defaults to the query string.</summary>
    public OpenApiParameterLocation In { get; set; } = OpenApiParameterLocation.Query;

    /// <summary>CLR type used to derive the schema. Defaults to string.</summary>
    public Type? Type { get; set; }

    /// <summary>Whether the parameter must be supplied. Path parameters are always required.</summary>
    public bool Required { get; set; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; set; }

    public OpenApiParameterAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Excludes an endpoint from the generated document — for internal, diagnostic, or deliberately
/// undocumented routes. The endpoint still serves traffic as normal.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class OpenApiIgnoreAttribute : Attribute
{
}
