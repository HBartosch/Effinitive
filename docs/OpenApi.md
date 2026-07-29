# OpenAPI / Swagger in EffinitiveFramework

EffinitiveFramework can describe itself. `UseOpenApi()` serves an OpenAPI 3.0.3 document at
`/openapi/v1.json` and a Swagger UI page at `/swagger`, so the API can be explored in a browser and
consumed by Postman, client generators, and API gateways.

The document is generated **once at startup** from the frozen route table and serialized to a byte
buffer, so serving it is a path comparison and a buffer write. There is no per-request reflection and no
cost at all when the feature is not enabled.

## Enabling it

```csharp
var app = EffinitiveApp.Create()
    .UseOpenApi(api =>
    {
        api.Title = "Orders API";
        api.Version = "v1";
        api.Description = "Order management endpoints.";
        api.AddJwtBearer();
    })
    .MapEndpoints()
    .Build();
```

Every registered endpoint is documented automatically — route, path parameters, request body, response
type, and content type all come from metadata the framework already resolved when it registered the
endpoint. The attributes below only add what cannot be inferred.

## What is inferred without any attributes

| Documented | Inferred from |
|---|---|
| Path and HTTP method | The endpoint's `Route` / `Method` |
| Path parameters and their types | `{placeholders}` in the route, typed from the matching request property |
| Request body schema | `TRequest`, skipped for `EmptyRequest` and no-request endpoints |
| 200 response schema | `TResponse` |
| Content type | The endpoint's `ContentType` property |
| Property names | The server's `JsonSerializerOptions` — camelCase by default |
| Schema constraints | DataAnnotations already used for validation |
| `400` / `404` / `401` responses | Whether the operation takes a body, has path parameters, or is `[Authorize]` |
| Operation id | Type name minus the `Endpoint` suffix |
| Tag | First meaningful path segment, unless overridden |

## Adding what can't be inferred

### `[OpenApiOperation]`

```csharp
[OpenApiOperation(
    Summary = "Get a product",
    Description = "Fetches one product by id.",
    Tags = "Catalog")]
public class GetProductEndpoint : EndpointBase<GetProductRequest, Product> { ... }
```

`Summary`, `Description`, `Tags` (comma-separated), `OperationId`, `Deprecated`.

### `[OpenApiResponse]`

Repeatable, one per status code. A 4xx/5xx without an explicit type documents the framework's
`ProblemDetails`, which is what the server actually returns.

```csharp
[OpenApiResponse(404, Description = "No product with that id")]
[OpenApiResponse(409, typeof(ConflictDetails), Description = "SKU already exists")]
```

### `[OpenApiParameter]`

Query and header parameters **must be declared**. The framework binds route parameters automatically,
but query strings and headers are read imperatively through `HttpContext.Query` and
`HttpContext.Headers`, so there is nothing for the generator to reflect over.

```csharp
[OpenApiParameter("page", Type = typeof(int), Description = "1-based page number")]
[OpenApiParameter("X-Tenant", In = OpenApiParameterLocation.Header, Required = true)]
public class ListProductsEndpoint : NoRequestEndpointBase<Product[]> { ... }
```

### `[OpenApiIgnore]`

Leaves an endpoint out of the document. It still serves traffic.

## Schemas

Complex types become entries in `components/schemas` and are referenced by `$ref`. Self-referencing types
terminate correctly. Property names follow the server's configured naming policy, so the schema matches
the wire format rather than the CLR names.

DataAnnotations are mirrored into the schema, so the document states the same rules the validation
middleware enforces:

| Attribute | Schema keyword |
|---|---|
| `[Required]` | adds to `required` |
| `[Range]` | `minimum` / `maximum` |
| `[StringLength]` | `maxLength` / `minLength` |
| `[MinLength]` / `[MaxLength]` | `minLength` / `maxLength`, or `minItems` / `maxItems` on arrays |
| `[RegularExpression]` | `pattern` |
| `[EmailAddress]` | `format: email` |

`[JsonPropertyName]` and `[JsonIgnore]` are honored. Enums are documented as integers by default,
matching `System.Text.Json`; register a `JsonStringEnumConverter` and they become string enums.

## Security

Register the schemes your app uses and operations carrying `[Authorize]` reference them automatically.
`[AllowAnonymous]` emits an empty requirement, which is the spec's way of clearing it.

```csharp
.UseOpenApi(api =>
{
    api.AddJwtBearer();                    // Authorization: Bearer <token>
    api.AddApiKey(headerName: "X-API-Key"); // matches ApiKeyAuthenticationHandler's default
})
```

Role and policy requirements from `[Authorize(Roles = "admin")]` are not expressed as OAuth2 scopes —
they are a different concept, and faking them as scopes would mislead client generators.

## The UI page

`/swagger` is a small HTML shell that loads Swagger UI from a CDN, which means **it needs internet access
to render**. The JSON document itself never does.

To serve the UI from your own origin or run air-gapped, drop the `swagger-ui-dist` files somewhere your
app serves and repoint the base URL:

```csharp
.UseStaticFiles("wwwroot")
.UseOpenApi(api => api.SwaggerUiCdnBase = "/static/swagger-ui")
```

Set `UiEnabled = false` to serve only the JSON.

## Options

| Option | Default | Meaning |
|---|---|---|
| `Title` / `Version` / `Description` | "API" / "v1" | Document metadata |
| `Servers` | empty | Emitted as `servers`; when empty, tools resolve paths relative to where they fetched the document |
| `DocumentPath` | `/openapi/v1.json` | Where the JSON is served |
| `UiPath` | `/swagger` | Where the UI is served |
| `UiEnabled` | `true` | Whether to serve the UI at all |
| `SwaggerUiCdnBase` | unpkg | Base URL for the UI assets |
| `SecuritySchemes` | empty | Schemes to advertise; use `AddJwtBearer()` / `AddApiKey()` |
| `IncludeProblemDetailsResponses` | `true` | Document the error responses the server produces on its own |

## What is not documented

- **WebSocket routes** — OpenAPI 3.0 cannot express them.
- **SSE endpoints** — documented as `text/event-stream` with no response schema, since they stream
  indefinitely rather than returning a payload.
- **`RawResponse` endpoints** — the declared content type is documented with no schema, because the
  payload is pre-built bytes chosen at runtime. Add `[OpenApiResponse]` to describe it.
- **Delegate-registered routes** — no type to hang metadata on, so the operation appears without schemas.

## Validating the output

The generated document is standards-conformant:

```bash
npx @redocly/cli lint openapi.json --extends=minimal
```

Redocly's default `recommended` ruleset additionally requires a summary and a security scheme on *every*
operation. Those are style opinions rather than spec requirements — add `[OpenApiOperation(Summary =
"...")]` to your endpoints if you want to satisfy them.

## Try it

The sample app (`samples/EffinitiveFramework.Sample`) has it enabled:

```bash
curl http://localhost:5000/openapi/v1.json
```

Then open <http://localhost:5000/swagger> in a browser.
