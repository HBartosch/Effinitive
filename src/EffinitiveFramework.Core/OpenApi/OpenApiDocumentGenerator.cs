using System.Globalization;
using System.Reflection;
using System.Text.Json;
using EffinitiveFramework.Core.Authorization;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.OpenApi;

/// <summary>
/// Builds an OpenAPI 3.0.3 document from the router's registered routes.
/// <para>
/// Runs once during <c>Build()</c>, after <c>Router.Freeze()</c>. Everything it needs was already
/// resolved at registration time — route patterns, endpoint types, and the compiled
/// <see cref="EndpointInvoker"/> carrying request/response types and route-parameter setters — so this
/// is a projection of existing metadata rather than a second reflection pass over the assembly.
/// </para>
/// </summary>
internal sealed class OpenApiDocumentGenerator
{
    private readonly OpenApiOptions _options;
    private readonly JsonSerializerOptions? _jsonOptions;

    public OpenApiDocumentGenerator(OpenApiOptions options, JsonSerializerOptions? jsonOptions)
    {
        _options = options;
        _jsonOptions = jsonOptions;
    }

    public OpenApiDocumentObject Generate(IReadOnlyList<RouteDescriptor> routes)
    {
        var schemaGenerator = new JsonSchemaGenerator(_jsonOptions);

        var document = new OpenApiDocumentObject
        {
            Info = new OpenApiInfoObject
            {
                Title = _options.Title,
                Version = _options.Version,
                Description = _options.Description
            }
        };

        if (_options.Servers.Count > 0)
        {
            document.Servers = _options.Servers
                .Select(s => new OpenApiServerObject { Url = s.Url, Description = s.Description })
                .ToList();
        }

        foreach (var route in routes)
        {
            if (ShouldSkip(route))
                continue;

            var operation = BuildOperation(route, schemaGenerator);
            var path = NormalizePath(route.Pattern);

            if (!document.Paths.TryGetValue(path, out var pathItem))
            {
                pathItem = new OpenApiPathItemObject();
                document.Paths[path] = pathItem;
            }

            pathItem.Set(route.Method, operation);
        }

        BuildComponents(document, schemaGenerator);
        return document;
    }

    private static bool ShouldSkip(RouteDescriptor route)
    {
        // Delegate-registered routes have no type to hang metadata on.
        if (route.EndpointType == null)
            return false;

        return route.EndpointType.GetCustomAttribute<OpenApiIgnoreAttribute>() != null;
    }

    // ── Operation ───────────────────────────────────────────────────────────────────────────────

    private OpenApiOperationObject BuildOperation(RouteDescriptor route, JsonSchemaGenerator schemas)
    {
        var endpointType = route.EndpointType;
        var metadata = endpointType?.GetCustomAttribute<OpenApiOperationAttribute>();

        var operation = new OpenApiOperationObject
        {
            Summary = metadata?.Summary,
            Description = metadata?.Description,
            OperationId = metadata?.OperationId ?? BuildOperationId(route),
            Deprecated = metadata?.Deprecated == true ? true : null,
            Tags = BuildTags(route, metadata)
        };

        var parameters = new List<OpenApiParameterObject>();
        AddPathParameters(route, parameters, schemas);
        AddDeclaredParameters(endpointType, parameters, schemas);
        if (parameters.Count > 0)
            operation.Parameters = parameters;

        operation.RequestBody = BuildRequestBody(route, schemas);
        BuildResponses(route, operation, schemas);
        operation.Security = BuildSecurity(endpointType);

        return operation;
    }

    /// <summary>
    /// Path parameters come from the <c>{placeholders}</c> in the pattern. Their types are read from
    /// the invoker's route-parameter setters, which already hold the <see cref="PropertyInfo"/> the
    /// binder writes into — so <c>/users/{id}</c> documents an integer when the request property is one.
    /// </summary>
    private static void AddPathParameters(RouteDescriptor route, List<OpenApiParameterObject> parameters, JsonSchemaGenerator schemas)
    {
        foreach (var name in ExtractPathParameterNames(route.Pattern))
        {
            OpenApiSchema schema;
            if (route.Invoker != null && route.Invoker.RouteParamSetters.TryGetValue(name, out var setter))
                schema = schemas.Generate(setter.Property.PropertyType);
            else
                schema = new OpenApiSchema { Type = "string" };

            parameters.Add(new OpenApiParameterObject
            {
                Name = name,
                In = "path",
                Required = true,   // always required, per the spec
                Schema = schema
            });
        }
    }

    private static void AddDeclaredParameters(Type? endpointType, List<OpenApiParameterObject> parameters, JsonSchemaGenerator schemas)
    {
        if (endpointType == null)
            return;

        foreach (var declared in endpointType.GetCustomAttributes<OpenApiParameterAttribute>())
        {
            var location = declared.In.ToString().ToLowerInvariant();

            // A path parameter derived from the pattern already exists; let the attribute enrich it
            // rather than emitting a duplicate, which is invalid.
            var existing = parameters.FirstOrDefault(p =>
                p.Name.Equals(declared.Name, StringComparison.OrdinalIgnoreCase) &&
                p.In.Equals(location, StringComparison.Ordinal));

            if (existing != null)
            {
                existing.Description ??= declared.Description;
                continue;
            }

            parameters.Add(new OpenApiParameterObject
            {
                Name = declared.Name,
                In = location,
                Required = declared.In == OpenApiParameterLocation.Path ? true : declared.Required ? true : null,
                Description = declared.Description,
                Schema = schemas.Generate(declared.Type ?? typeof(string))
            });
        }
    }

    private OpenApiRequestBodyObject? BuildRequestBody(RouteDescriptor route, JsonSchemaGenerator schemas)
    {
        var invoker = route.Invoker;
        if (invoker == null)
            return null;

        // NoRequest endpoints take only a CancellationToken; EmptyRequest is the marker for "no body".
        if (invoker.IsNoRequest || invoker.RequestType == typeof(EmptyRequest))
            return null;

        // GET and DELETE bodies are legal but not meaningful here — the framework binds route
        // parameters into the request object, so a GET's TRequest describes the path, not a payload.
        if (route.Method.Equals(HttpMethods.Get, StringComparison.OrdinalIgnoreCase) ||
            route.Method.Equals(HttpMethods.Head, StringComparison.OrdinalIgnoreCase) ||
            route.Method.Equals(HttpMethods.Delete, StringComparison.OrdinalIgnoreCase))
            return null;

        var contentType = MediaTypes.ApplicationJson;
        var schema = invoker.RequestType == typeof(string)
            ? new OpenApiSchema { Type = "string" }
            : invoker.RequestType == typeof(byte[])
                ? new OpenApiSchema { Type = "string", Format = "byte" }
                : schemas.Generate(invoker.RequestType);

        return new OpenApiRequestBodyObject
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaTypeObject>(StringComparer.Ordinal)
            {
                [contentType] = new OpenApiMediaTypeObject { Schema = schema }
            }
        };
    }

    private void BuildResponses(RouteDescriptor route, OpenApiOperationObject operation, JsonSchemaGenerator schemas)
    {
        var endpointType = route.EndpointType;
        var declared = endpointType?.GetCustomAttributes<OpenApiResponseAttribute>().ToList()
                       ?? new List<OpenApiResponseAttribute>();

        // Success response, unless the endpoint declared its own 2xx.
        if (!declared.Any(d => d.StatusCode is >= 200 and < 300))
        {
            var success = BuildSuccessResponse(route, schemas);
            if (success != null)
                operation.Responses["200"] = success;
        }

        foreach (var attribute in declared)
        {
            var key = attribute.StatusCode.ToString(CultureInfo.InvariantCulture);
            operation.Responses[key] = BuildDeclaredResponse(attribute, schemas);
        }

        if (_options.IncludeProblemDetailsResponses)
            AddProblemDetailsResponses(route, operation, schemas, declared);

        // A response object is required by the spec, so never leave it empty.
        if (operation.Responses.Count == 0)
            operation.Responses["200"] = new OpenApiResponseObject { Description = "Success" };
    }

    private OpenApiResponseObject? BuildSuccessResponse(RouteDescriptor route, JsonSchemaGenerator schemas)
    {
        var invoker = route.Invoker;

        if (invoker == null)
        {
            // A typed endpoint with no compiled invoker is an SSE endpoint: those register through the
            // GetMethod/GetRoute pattern and stream indefinitely rather than returning a payload.
            if (route.EndpointType != null)
            {
                return new OpenApiResponseObject
                {
                    Description = "Event stream",
                    Content = new Dictionary<string, OpenApiMediaTypeObject>(StringComparer.Ordinal)
                    {
                        [MediaTypes.TextEventStream] = new OpenApiMediaTypeObject()
                    }
                };
            }

            // Delegate-registered route: no type to reflect over, so the payload is undescribed.
            return new OpenApiResponseObject { Description = "Success" };
        }

        var responseType = invoker.ResponseType;
        var contentType = ResolveContentType(route, invoker);

        // RawResponse carries pre-built bytes with a content type chosen at runtime — there is no
        // schema to describe. Say so rather than documenting RawResponse's own properties.
        if (responseType == typeof(RawResponse))
        {
            return new OpenApiResponseObject
            {
                Description = "Success",
                Content = new Dictionary<string, OpenApiMediaTypeObject>(StringComparer.Ordinal)
                {
                    [contentType] = new OpenApiMediaTypeObject()
                }
            };
        }

        if (responseType == typeof(void))
            return new OpenApiResponseObject { Description = "Success" };

        var schema = responseType == typeof(string)
            ? new OpenApiSchema { Type = "string" }
            : schemas.Generate(responseType);

        return new OpenApiResponseObject
        {
            Description = "Success",
            Content = new Dictionary<string, OpenApiMediaTypeObject>(StringComparer.Ordinal)
            {
                [contentType] = new OpenApiMediaTypeObject { Schema = schema }
            }
        };
    }

    /// <summary>
    /// Reads the endpoint's ContentType through the compiled getter the invoker already built, so a
    /// text/plain or text/html endpoint documents what it really returns. Requires an instance, which
    /// we only attempt for types with a parameterless constructor — endpoints with injected
    /// dependencies fall back to JSON.
    /// </summary>
    private static string ResolveContentType(RouteDescriptor route, EndpointInvoker invoker)
    {
        if (route.EndpointType == null)
            return MediaTypes.ApplicationJson;

        try
        {
            if (route.EndpointType.GetConstructor(Type.EmptyTypes) == null)
                return MediaTypes.ApplicationJson;

            var instance = Activator.CreateInstance(route.EndpointType);
            if (instance == null)
                return MediaTypes.ApplicationJson;

            var contentType = invoker.GetContentType(instance);
            return string.IsNullOrEmpty(contentType) ? MediaTypes.ApplicationJson : contentType;
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException)
        {
            // Constructing the endpoint is a convenience, never a requirement.
            return MediaTypes.ApplicationJson;
        }
    }

    private OpenApiResponseObject BuildDeclaredResponse(OpenApiResponseAttribute attribute, JsonSchemaGenerator schemas)
    {
        var description = attribute.Description ?? ReasonPhrase(attribute.StatusCode);

        if (attribute.StatusCode == 204)
            return new OpenApiResponseObject { Description = description };

        var isError = attribute.StatusCode >= 400;
        var type = attribute.Type ?? (isError ? typeof(ProblemDetails) : null);

        if (type == null)
            return new OpenApiResponseObject { Description = description };

        var contentType = attribute.ContentType
                          ?? (isError && attribute.Type == null
                                ? MediaTypes.ApplicationProblemJson
                                : MediaTypes.ApplicationJson);

        return new OpenApiResponseObject
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaTypeObject>(StringComparer.Ordinal)
            {
                [contentType] = new OpenApiMediaTypeObject { Schema = schemas.Generate(type) }
            }
        };
    }

    /// <summary>
    /// Documents the error responses the server produces on its own: routing failures, body-parse
    /// failures, and unhandled exceptions all return <c>application/problem+json</c>.
    /// </summary>
    private static void AddProblemDetailsResponses(
        RouteDescriptor route,
        OpenApiOperationObject operation,
        JsonSchemaGenerator schemas,
        List<OpenApiResponseAttribute> declared)
    {
        // Only where the framework can actually produce them, and never overriding an explicit
        // declaration.
        var hasBody = operation.RequestBody != null;
        var hasPathParams = operation.Parameters?.Any(p => p.In == "path") == true;

        if (hasBody && !declared.Any(d => d.StatusCode == 400))
            operation.Responses["400"] = ProblemResponse("Malformed request body", schemas);

        if (hasPathParams && !declared.Any(d => d.StatusCode == 404))
            operation.Responses["404"] = ProblemResponse("Resource not found", schemas);

        if (route.EndpointType?.GetCustomAttribute<AuthorizeAttribute>() != null &&
            !declared.Any(d => d.StatusCode == 401))
            operation.Responses["401"] = ProblemResponse("Authentication required", schemas);
    }

    private static OpenApiResponseObject ProblemResponse(string description, JsonSchemaGenerator schemas)
        => new()
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaTypeObject>(StringComparer.Ordinal)
            {
                [MediaTypes.ApplicationProblemJson] = new OpenApiMediaTypeObject
                {
                    Schema = schemas.Generate(typeof(ProblemDetails))
                }
            }
        };

    // ── Security ────────────────────────────────────────────────────────────────────────────────

    private List<Dictionary<string, string[]>>? BuildSecurity(Type? endpointType)
    {
        if (endpointType == null || _options.SecuritySchemes.Count == 0)
            return null;

        // An empty array is the spec's way of saying "no security here", which is what
        // [AllowAnonymous] means when a document-level requirement exists.
        if (endpointType.GetCustomAttribute<AllowAnonymousAttribute>() != null)
            return new List<Dictionary<string, string[]>>();

        var authorize = endpointType.GetCustomAttribute<AuthorizeAttribute>();
        if (authorize == null)
            return null;

        // Scopes are the OAuth2 concept; role and policy requirements do not map onto them, so they
        // are surfaced in the operation description instead of being faked as scopes.
        var requirement = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var scheme in _options.SecuritySchemes)
            requirement[scheme.Name] = Array.Empty<string>();

        return new List<Dictionary<string, string[]>> { requirement };
    }

    // ── Components ──────────────────────────────────────────────────────────────────────────────

    private void BuildComponents(OpenApiDocumentObject document, JsonSchemaGenerator schemas)
    {
        OpenApiComponentsObject? components = null;

        if (schemas.Schemas.Count > 0)
        {
            components = new OpenApiComponentsObject
            {
                Schemas = schemas.Schemas.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            };
        }

        if (_options.SecuritySchemes.Count > 0)
        {
            components ??= new OpenApiComponentsObject();
            components.SecuritySchemes = new Dictionary<string, OpenApiSecuritySchemeObject>(StringComparer.Ordinal);

            foreach (var scheme in _options.SecuritySchemes)
            {
                components.SecuritySchemes[scheme.Name] = new OpenApiSecuritySchemeObject
                {
                    Type = scheme.Type,
                    Scheme = scheme.Scheme,
                    BearerFormat = scheme.BearerFormat,
                    Name = scheme.ParameterName,
                    In = scheme.In,
                    Description = scheme.Description
                };
            }
        }

        document.Components = components;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Route patterns are already in OpenAPI's <c>{param}</c> form; just ensure a leading slash.</summary>
    private static string NormalizePath(string pattern)
        => pattern.StartsWith('/') ? pattern : "/" + pattern;

    internal static List<string> ExtractPathParameterNames(string pattern)
    {
        var names = new List<string>();
        var span = pattern.AsSpan();

        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != '{')
                continue;

            var close = span[i..].IndexOf('}');
            if (close < 0)
                break;

            var name = span.Slice(i + 1, close - 1);
            if (!name.IsEmpty)
                names.Add(name.ToString());

            i += close;
        }

        return names;
    }

    /// <summary>
    /// Operation ids must be unique and are used verbatim as method names by client generators, so
    /// prefer the endpoint's type name (minus the conventional "Endpoint" suffix) over the route.
    /// </summary>
    private static string BuildOperationId(RouteDescriptor route)
    {
        if (route.EndpointType != null)
        {
            var name = route.EndpointType.Name;
            if (name.EndsWith("Endpoint", StringComparison.Ordinal) && name.Length > "Endpoint".Length)
                name = name[..^"Endpoint".Length];
            return name;
        }

        var segments = route.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('{', '}'))
            .ToArray();

        return route.Method.ToLowerInvariant() + string.Concat(segments.Select(Capitalize));
    }

    private static List<string> BuildTags(RouteDescriptor route, OpenApiOperationAttribute? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.Tags))
        {
            return metadata!.Tags!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        // Default: group by the first non-parameter path segment, so /api/users/{id} lands under
        // "api"'s successor rather than everything piling into one bucket.
        var segments = route.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment.StartsWith('{'))
                continue;
            if (segment.Equals("api", StringComparison.OrdinalIgnoreCase))
                continue;
            return new List<string> { Capitalize(segment) };
        }

        return new List<string> { "Default" };
    }

    private static string Capitalize(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Response"
    };
}
