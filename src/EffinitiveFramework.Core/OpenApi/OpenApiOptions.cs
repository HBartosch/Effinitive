namespace EffinitiveFramework.Core.OpenApi;

/// <summary>
/// A server the API is reachable at, emitted into the document's <c>servers</c> array.
/// </summary>
public sealed class OpenApiServer
{
    public string Url { get; set; } = "/";
    public string? Description { get; set; }
}

/// <summary>
/// An authentication scheme advertised in <c>components/securitySchemes</c> and referenced by
/// operations carrying <c>[Authorize]</c>.
/// </summary>
public sealed class OpenApiSecurityScheme
{
    /// <summary>Key the scheme is registered under, e.g. "bearerAuth".</summary>
    public string Name { get; set; } = "bearerAuth";

    /// <summary>OpenAPI scheme type: "http" or "apiKey".</summary>
    public string Type { get; set; } = "http";

    /// <summary>For <c>type: http</c> — the HTTP authentication scheme, e.g. "bearer".</summary>
    public string? Scheme { get; set; }

    /// <summary>For bearer schemes — a hint at the token format, e.g. "JWT".</summary>
    public string? BearerFormat { get; set; }

    /// <summary>For <c>type: apiKey</c> — the header or query parameter carrying the key.</summary>
    public string? ParameterName { get; set; }

    /// <summary>For <c>type: apiKey</c> — "header" or "query".</summary>
    public string? In { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Configuration for <c>UseOpenApi()</c>. The document is generated once at startup, so everything here
/// is read during <c>Build()</c> and never on the request path.
/// </summary>
public sealed class OpenApiOptions
{
    /// <summary>API title shown in the document and UI.</summary>
    public string Title { get; set; } = "API";

    /// <summary>API version string (the API's own version, not the OpenAPI spec version).</summary>
    public string Version { get; set; } = "v1";

    /// <summary>Longer API description. Rendered as Markdown by most viewers.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Servers the API is reachable at. Left empty, the document omits the <c>servers</c> array and
    /// tools resolve paths relative to wherever they fetched the document from — usually what you want.
    /// </summary>
    public List<OpenApiServer> Servers { get; } = new();

    /// <summary>Path the JSON document is served from.</summary>
    public string DocumentPath { get; set; } = "/openapi/v1.json";

    /// <summary>Path the HTML UI is served from.</summary>
    public string UiPath { get; set; } = "/swagger";

    /// <summary>Whether to serve the HTML UI page at all. The JSON document is always served.</summary>
    public bool UiEnabled { get; set; } = true;

    /// <summary>
    /// Base URL for the Swagger UI assets the HTML page loads. Defaults to a public CDN, which means
    /// the UI page needs internet access to render. Point this at your own copy of swagger-ui-dist to
    /// serve it from your own origin or to work air-gapped — for example, drop the dist folder in
    /// wwwroot and set this to "/static/swagger-ui".
    /// </summary>
    public string SwaggerUiCdnBase { get; set; } = "https://unpkg.com/swagger-ui-dist@5";

    /// <summary>
    /// Authentication schemes to advertise. Operations carrying <c>[Authorize]</c> reference every
    /// registered scheme; <c>[AllowAnonymous]</c> clears the requirement.
    /// </summary>
    public List<OpenApiSecurityScheme> SecuritySchemes { get; } = new();

    /// <summary>
    /// When true (the default), 4xx/5xx responses are documented with the framework's
    /// <c>ProblemDetails</c> schema, matching what the server actually returns for routing,
    /// validation, and unhandled-exception failures.
    /// </summary>
    public bool IncludeProblemDetailsResponses { get; set; } = true;

    /// <summary>
    /// Registers the bearer-token scheme matching <c>JwtAuthenticationHandler</c>'s defaults
    /// (<c>Authorization: Bearer &lt;token&gt;</c>).
    /// </summary>
    public OpenApiOptions AddJwtBearer(string name = "bearerAuth", string? description = null)
    {
        SecuritySchemes.Add(new OpenApiSecurityScheme
        {
            Name = name,
            Type = "http",
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = description
        });
        return this;
    }

    /// <summary>
    /// Registers the API-key scheme matching <c>ApiKeyAuthenticationHandler</c>'s default header
    /// (<c>X-API-Key</c>).
    /// </summary>
    public OpenApiOptions AddApiKey(string name = "apiKey", string headerName = "X-API-Key", string? description = null)
    {
        SecuritySchemes.Add(new OpenApiSecurityScheme
        {
            Name = name,
            Type = "apiKey",
            ParameterName = headerName,
            In = "header",
            Description = description
        });
        return this;
    }
}
