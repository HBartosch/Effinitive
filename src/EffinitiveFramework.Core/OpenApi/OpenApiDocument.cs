using System.Text.Json;
using System.Text.Json.Serialization;

namespace EffinitiveFramework.Core.OpenApi;

// Serialization model for an OpenAPI 3.0.3 document. Type names carry the "Object" suffix used by the
// specification itself ("Operation Object", "Parameter Object") both because that is the spec's own
// vocabulary and because it keeps these distinct from the attributes and options classes of the same
// concept. Everything here is internal: it is a serialization detail, not public API.
//
// Null properties are omitted at write time, so a mostly-empty operation serializes to a couple of keys
// rather than a wall of nulls.

internal sealed class OpenApiDocumentObject
{
    [JsonPropertyName("openapi")]
    public string OpenApi { get; set; } = "3.0.3";

    public OpenApiInfoObject Info { get; set; } = new();

    public List<OpenApiServerObject>? Servers { get; set; }

    public Dictionary<string, OpenApiPathItemObject> Paths { get; set; } = new(StringComparer.Ordinal);

    public OpenApiComponentsObject? Components { get; set; }

    /// <summary>
    /// Serializer settings shared by the whole document: camelCase keys, no nulls, and relaxed escaping
    /// so descriptions containing quotes or non-ASCII text stay readable.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}

internal sealed class OpenApiInfoObject
{
    public string Title { get; set; } = "API";
    public string Version { get; set; } = "v1";
    public string? Description { get; set; }
}

internal sealed class OpenApiServerObject
{
    public string Url { get; set; } = "/";
    public string? Description { get; set; }
}

/// <summary>
/// The operations available on one path. Only the verbs actually registered are populated.
/// </summary>
internal sealed class OpenApiPathItemObject
{
    public OpenApiOperationObject? Get { get; set; }
    public OpenApiOperationObject? Post { get; set; }
    public OpenApiOperationObject? Put { get; set; }
    public OpenApiOperationObject? Delete { get; set; }
    public OpenApiOperationObject? Patch { get; set; }
    public OpenApiOperationObject? Head { get; set; }
    public OpenApiOperationObject? Options { get; set; }

    /// <summary>Assigns an operation to the slot for the given method. Unknown verbs are ignored.</summary>
    public void Set(string method, OpenApiOperationObject operation)
    {
        switch (method.ToUpperInvariant())
        {
            case "GET": Get = operation; break;
            case "POST": Post = operation; break;
            case "PUT": Put = operation; break;
            case "DELETE": Delete = operation; break;
            case "PATCH": Patch = operation; break;
            case "HEAD": Head = operation; break;
            case "OPTIONS": Options = operation; break;
        }
    }
}

internal sealed class OpenApiOperationObject
{
    public List<string>? Tags { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? OperationId { get; set; }
    public bool? Deprecated { get; set; }
    public List<OpenApiParameterObject>? Parameters { get; set; }
    public OpenApiRequestBodyObject? RequestBody { get; set; }
    public Dictionary<string, OpenApiResponseObject> Responses { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Security requirements. An empty list (not null) is meaningful in OpenAPI: it explicitly clears
    /// any document-level requirement, which is how <c>[AllowAnonymous]</c> is expressed.
    /// </summary>
    public List<Dictionary<string, string[]>>? Security { get; set; }
}

internal sealed class OpenApiParameterObject
{
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("in")]
    public string In { get; set; } = "query";

    public bool? Required { get; set; }
    public string? Description { get; set; }
    public OpenApiSchema? Schema { get; set; }
}

internal sealed class OpenApiRequestBodyObject
{
    public string? Description { get; set; }
    public bool? Required { get; set; }
    public Dictionary<string, OpenApiMediaTypeObject> Content { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class OpenApiResponseObject
{
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, OpenApiMediaTypeObject>? Content { get; set; }
}

internal sealed class OpenApiMediaTypeObject
{
    public OpenApiSchema? Schema { get; set; }
}

internal sealed class OpenApiComponentsObject
{
    public Dictionary<string, OpenApiSchema>? Schemas { get; set; }
    public Dictionary<string, OpenApiSecuritySchemeObject>? SecuritySchemes { get; set; }
}

internal sealed class OpenApiSecuritySchemeObject
{
    public string Type { get; set; } = "http";
    public string? Scheme { get; set; }
    public string? BearerFormat { get; set; }

    /// <summary>For apiKey schemes: the header or query parameter name.</summary>
    public string? Name { get; set; }

    [JsonPropertyName("in")]
    public string? In { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// A JSON Schema node as OpenAPI 3.0 constrains it — notably <c>nullable</c> as a boolean rather than
/// JSON Schema 2020-12's type unions.
/// </summary>
internal sealed class OpenApiSchema
{
    [JsonPropertyName("$ref")]
    public string? Ref { get; set; }

    public string? Type { get; set; }
    public string? Format { get; set; }
    public bool? Nullable { get; set; }
    public string? Description { get; set; }

    public OpenApiSchema? Items { get; set; }
    public Dictionary<string, OpenApiSchema>? Properties { get; set; }
    public OpenApiSchema? AdditionalProperties { get; set; }
    public List<string>? Required { get; set; }

    [JsonPropertyName("enum")]
    public List<object>? Enum { get; set; }

    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public int? MinItems { get; set; }
    public int? MaxItems { get; set; }
    public string? Pattern { get; set; }
}
