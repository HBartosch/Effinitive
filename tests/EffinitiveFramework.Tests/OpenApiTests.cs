using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Authorization;
using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.OpenApi;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for OpenAPI document generation. Schema and document generators are exercised directly;
/// document-level assertions run against the serialized JSON so serialization is covered too.
/// </summary>
public class OpenApiTests
{
    // Endpoint types are public: the compiled expression trees in EndpointInvoker.Build would hit
    // visibility checks against private nested types.

    public enum ProductStatus { Draft, Active, Discontinued }

    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public ProductStatus Status { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public DateTime CreatedAt { get; set; }
        public Category? Category { get; set; }
    }

    /// <summary>Self-referencing on purpose — the generator must not recurse forever.</summary>
    public class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Category? Parent { get; set; }
    }

    public class CreateProductRequest
    {
        [Required]
        [StringLength(50, MinimumLength = 3)]
        public string Name { get; set; } = string.Empty;

        [Range(0, 1000)]
        public decimal Price { get; set; }

        [RegularExpression("^[A-Z]{3}-[0-9]{4}$")]
        public string Sku { get; set; } = string.Empty;

        [EmailAddress]
        public string? ContactEmail { get; set; }

        [JsonPropertyName("legacy_code")]
        public string? LegacyCode { get; set; }

        [JsonIgnore]
        public string? Internal { get; set; }

        public int? OptionalCount { get; set; }
    }

    public class GetProductRequest
    {
        public int Id { get; set; }
    }

    public class HealthResponse
    {
        public string Status { get; set; } = "ok";
    }

    public class TagCounts
    {
        public Dictionary<string, int> Counts { get; set; } = new();
    }

    [OpenApiOperation(Summary = "Get a product", Description = "Fetches one product by id.", Tags = "Catalog")]
    [OpenApiResponse(404, Description = "No product with that id")]
    public class GetProductEndpoint : EndpointBase<GetProductRequest, Product>
    {
        protected override string Method => "GET";
        protected override string Route => "/products/{id}";
        public override ValueTask<Product> HandleAsync(GetProductRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new Product());
    }

    [OpenApiParameter("page", Type = typeof(int), Description = "1-based page number")]
    [OpenApiParameter("X-Tenant", In = OpenApiParameterLocation.Header, Required = true)]
    public class ListProductsEndpoint : NoRequestEndpointBase<Product[]>
    {
        protected override string Method => "GET";
        protected override string Route => "/products";
        public override ValueTask<Product[]> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Array.Empty<Product>());
    }

    public class CreateProductEndpoint : AsyncEndpointBase<CreateProductRequest, Product>
    {
        protected override string Method => "POST";
        protected override string Route => "/products";
        public override Task<Product> HandleAsync(CreateProductRequest request, CancellationToken ct = default)
            => Task.FromResult(new Product());
    }

    [Authorize(Roles = "admin")]
    public class DeleteProductEndpoint : EndpointBase<GetProductRequest, HealthResponse>
    {
        protected override string Method => "DELETE";
        protected override string Route => "/products/{id}";
        public override ValueTask<HealthResponse> HandleAsync(GetProductRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new HealthResponse());
    }

    [OpenApiIgnore]
    public class HiddenEndpoint : NoRequestEndpointBase<HealthResponse>
    {
        protected override string Method => "GET";
        protected override string Route => "/internal/diagnostics";
        public override ValueTask<HealthResponse> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new HealthResponse());
    }

    public class PlainTextEndpoint : NoRequestEndpointBase<string>
    {
        protected override string Method => "GET";
        protected override string Route => "/ping";
        protected override string ContentType => MediaTypes.TextPlain;
        public override ValueTask<string> HandleAsync(CancellationToken ct = default)
            => ValueTask.FromResult("pong");
    }

    // ── Helpers ──

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static RouteDescriptor Route(string method, string pattern, Type endpointType)
        => new(method, pattern, endpointType, EndpointInvoker.Build(endpointType));

    private static JsonElement GenerateDocument(params RouteDescriptor[] routes)
        => GenerateDocument(new OpenApiOptions { Title = "Test API", Version = "v1" }, routes);

    private static JsonElement GenerateDocument(OpenApiOptions options, params RouteDescriptor[] routes)
    {
        var handler = new OpenApiHandler(options, routes, CamelCase);
        return JsonDocument.Parse(handler.DocumentBytes.ToArray()).RootElement.Clone();
    }

    private static JsonElement Operation(JsonElement document, string path, string method)
        => document.GetProperty("paths").GetProperty(path).GetProperty(method);

    // ── Schema generation ──

    [Fact]
    public void Schema_UsesTheConfiguredNamingPolicy()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(Product));

        var product = generator.Schemas["Product"];

        Assert.NotNull(product.Properties);
        Assert.True(product.Properties!.ContainsKey("createdAt"));
        Assert.False(product.Properties.ContainsKey("CreatedAt"));
    }

    [Fact]
    public void Schema_WithoutNamingPolicy_KeepsClrNames()
    {
        var generator = new JsonSchemaGenerator(new JsonSerializerOptions());
        generator.Generate(typeof(Product));

        Assert.True(generator.Schemas["Product"].Properties!.ContainsKey("CreatedAt"));
    }

    [Fact]
    public void Schema_MapsPrimitiveTypesAndFormats()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(Product));
        var properties = generator.Schemas["Product"].Properties!;

        Assert.Equal("integer", properties["id"].Type);
        Assert.Equal("int32", properties["id"].Format);
        Assert.Equal("string", properties["name"].Type);
        Assert.Equal("number", properties["price"].Type);
        Assert.Equal("string", properties["createdAt"].Type);
        Assert.Equal("date-time", properties["createdAt"].Format);
    }

    [Fact]
    public void Schema_MapsCollectionsToArrays()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(Product));

        var tags = generator.Schemas["Product"].Properties!["tags"];
        Assert.Equal("array", tags.Type);
        Assert.Equal("string", tags.Items!.Type);
    }

    [Fact]
    public void Schema_MapsStringKeyedDictionaryToAdditionalProperties()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(TagCounts));

        var counts = generator.Schemas["TagCounts"].Properties!["counts"];
        Assert.Equal("object", counts.Type);
        Assert.Equal("integer", counts.AdditionalProperties!.Type);
    }

    [Fact]
    public void Schema_EnumDefaultsToIntegerValues()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(Product));

        var status = generator.Schemas["Product"].Properties!["status"];
        Assert.Equal("integer", status.Type);
        Assert.Equal(3, status.Enum!.Count);
    }

    [Fact]
    public void Schema_EnumUsesNamesWhenStringConverterIsRegistered()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());

        var generator = new JsonSchemaGenerator(options);
        generator.Generate(typeof(Product));

        var status = generator.Schemas["Product"].Properties!["status"];
        Assert.Equal("string", status.Type);
        Assert.Contains("Active", status.Enum!.Select(v => v?.ToString()));
    }

    [Fact]
    public void Schema_SelfReferencingTypeTerminatesAndUsesRef()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(Category));

        var category = generator.Schemas["Category"];
        Assert.Equal("#/components/schemas/Category", category.Properties!["parent"].Ref);
        Assert.Single(generator.Schemas, s => s.Key == "Category");
    }

    [Fact]
    public void Schema_NestedComplexTypeBecomesItsOwnComponent()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(Product));

        Assert.True(generator.Schemas.ContainsKey("Category"));
        Assert.Equal("#/components/schemas/Category", generator.Schemas["Product"].Properties!["category"].Ref);
    }

    [Fact]
    public void Schema_HonorsJsonPropertyNameAndJsonIgnore()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(CreateProductRequest));
        var properties = generator.Schemas["CreateProductRequest"].Properties!;

        Assert.True(properties.ContainsKey("legacy_code"));
        Assert.False(properties.ContainsKey("internal"));
    }

    [Fact]
    public void Schema_MirrorsDataAnnotationConstraints()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(CreateProductRequest));
        var schema = generator.Schemas["CreateProductRequest"];
        var properties = schema.Properties!;

        Assert.Contains("name", schema.Required!);
        Assert.Equal(50, properties["name"].MaxLength);
        Assert.Equal(3, properties["name"].MinLength);
        Assert.Equal(0d, properties["price"].Minimum);
        Assert.Equal(1000d, properties["price"].Maximum);
        Assert.Equal("^[A-Z]{3}-[0-9]{4}$", properties["sku"].Pattern);
        Assert.Equal("email", properties["contactEmail"].Format);
    }

    [Fact]
    public void Schema_NullableValueTypeIsMarkedNullable()
    {
        var generator = new JsonSchemaGenerator(CamelCase);
        generator.Generate(typeof(CreateProductRequest));

        var optional = generator.Schemas["CreateProductRequest"].Properties!["optionalCount"];
        Assert.Equal("integer", optional.Type);
        Assert.True(optional.Nullable);
    }

    // ── Document structure ──

    [Fact]
    public void Document_HasSpecVersionInfoAndPaths()
    {
        var document = GenerateDocument(Route("GET", "/products/{id}", typeof(GetProductEndpoint)));

        Assert.Equal("3.0.3", document.GetProperty("openapi").GetString());
        Assert.Equal("Test API", document.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", document.GetProperty("info").GetProperty("version").GetString());
        Assert.True(document.GetProperty("paths").TryGetProperty("/products/{id}", out _));
    }

    [Fact]
    public void Document_MergesMethodsOnTheSamePath()
    {
        var document = GenerateDocument(
            Route("GET", "/products", typeof(ListProductsEndpoint)),
            Route("POST", "/products", typeof(CreateProductEndpoint)));

        var path = document.GetProperty("paths").GetProperty("/products");
        Assert.True(path.TryGetProperty("get", out _));
        Assert.True(path.TryGetProperty("post", out _));
    }

    [Fact]
    public void Operation_UsesAttributeMetadata()
    {
        var document = GenerateDocument(Route("GET", "/products/{id}", typeof(GetProductEndpoint)));
        var operation = Operation(document, "/products/{id}", "get");

        Assert.Equal("Get a product", operation.GetProperty("summary").GetString());
        Assert.Equal("Fetches one product by id.", operation.GetProperty("description").GetString());
        Assert.Equal("Catalog", operation.GetProperty("tags")[0].GetString());
        Assert.Equal("GetProduct", operation.GetProperty("operationId").GetString());
    }

    [Fact]
    public void Operation_PathParameterIsTypedFromTheRequestProperty()
    {
        var document = GenerateDocument(Route("GET", "/products/{id}", typeof(GetProductEndpoint)));
        var parameter = Operation(document, "/products/{id}", "get").GetProperty("parameters")[0];

        Assert.Equal("id", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("integer", parameter.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Operation_DeclaredQueryAndHeaderParametersAreEmitted()
    {
        var document = GenerateDocument(Route("GET", "/products", typeof(ListProductsEndpoint)));
        var parameters = Operation(document, "/products", "get").GetProperty("parameters");

        var page = parameters.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "page");
        Assert.Equal("query", page.GetProperty("in").GetString());
        Assert.Equal("integer", page.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("1-based page number", page.GetProperty("description").GetString());

        var tenant = parameters.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "X-Tenant");
        Assert.Equal("header", tenant.GetProperty("in").GetString());
        Assert.True(tenant.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Operation_PostHasRequestBody()
    {
        var document = GenerateDocument(Route("POST", "/products", typeof(CreateProductEndpoint)));
        var body = Operation(document, "/products", "post").GetProperty("requestBody");

        var schemaRef = body.GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref").GetString();

        Assert.Equal("#/components/schemas/CreateProductRequest", schemaRef);
    }

    [Fact]
    public void Operation_GetHasNoRequestBody()
    {
        var document = GenerateDocument(Route("GET", "/products/{id}", typeof(GetProductEndpoint)));
        Assert.False(Operation(document, "/products/{id}", "get").TryGetProperty("requestBody", out _));
    }

    [Fact]
    public void Operation_NoRequestEndpointHasNoRequestBody()
    {
        var document = GenerateDocument(Route("GET", "/products", typeof(ListProductsEndpoint)));
        Assert.False(Operation(document, "/products", "get").TryGetProperty("requestBody", out _));
    }

    [Fact]
    public void Operation_SuccessResponseUsesTheResponseType()
    {
        var document = GenerateDocument(Route("GET", "/products/{id}", typeof(GetProductEndpoint)));

        var schemaRef = Operation(document, "/products/{id}", "get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString();

        Assert.Equal("#/components/schemas/Product", schemaRef);
    }

    [Fact]
    public void Operation_ArrayResponseIsDocumentedAsAnArray()
    {
        var document = GenerateDocument(Route("GET", "/products", typeof(ListProductsEndpoint)));

        var schema = Operation(document, "/products", "get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema");

        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/Product", schema.GetProperty("items").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Operation_DeclaredResponseIsAdded()
    {
        var document = GenerateDocument(Route("GET", "/products/{id}", typeof(GetProductEndpoint)));
        var responses = Operation(document, "/products/{id}", "get").GetProperty("responses");

        var notFound = responses.GetProperty("404");
        Assert.Equal("No product with that id", notFound.GetProperty("description").GetString());
        Assert.True(notFound.GetProperty("content").TryGetProperty("application/problem+json", out _));
    }

    [Fact]
    public void Operation_ContentTypeFollowsTheEndpoint()
    {
        var document = GenerateDocument(Route("GET", "/ping", typeof(PlainTextEndpoint)));

        var content = Operation(document, "/ping", "get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("text/plain", out _));
        Assert.False(content.TryGetProperty("application/json", out _));
    }

    [Fact]
    public void Operation_MalformedBodyIsDocumentedForOperationsWithABody()
    {
        var document = GenerateDocument(Route("POST", "/products", typeof(CreateProductEndpoint)));
        var responses = Operation(document, "/products", "post").GetProperty("responses");

        Assert.True(responses.TryGetProperty("400", out var badRequest));
        Assert.Equal(
            "#/components/schemas/ProblemDetails",
            badRequest.GetProperty("content").GetProperty("application/problem+json")
                      .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Operation_ProblemDetailsResponsesCanBeDisabled()
    {
        var options = new OpenApiOptions { IncludeProblemDetailsResponses = false };
        var document = GenerateDocument(options, Route("POST", "/products", typeof(CreateProductEndpoint)));

        Assert.False(Operation(document, "/products", "post").GetProperty("responses")
            .TryGetProperty("400", out _));
    }

    // ── Exclusions ──

    [Fact]
    public void OpenApiIgnore_ExcludesTheEndpoint()
    {
        var document = GenerateDocument(
            Route("GET", "/products", typeof(ListProductsEndpoint)),
            Route("GET", "/internal/diagnostics", typeof(HiddenEndpoint)));

        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/products", out _));
        Assert.False(paths.TryGetProperty("/internal/diagnostics", out _));
    }

    [Fact]
    public void DelegateRouteWithoutEndpointType_IsDocumentedWithoutSchemas()
    {
        var document = GenerateDocument(new RouteDescriptor("GET", "/legacy", null, null));

        var operation = Operation(document, "/legacy", "get");
        Assert.False(operation.TryGetProperty("requestBody", out _));
        Assert.Equal("Success", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("description").GetString());
    }

    // ── Security ──

    [Fact]
    public void Authorize_AddsSecurityRequirementAndScheme()
    {
        var options = new OpenApiOptions();
        options.AddJwtBearer();

        var document = GenerateDocument(options, Route("DELETE", "/products/{id}", typeof(DeleteProductEndpoint)));

        var security = Operation(document, "/products/{id}", "delete").GetProperty("security");
        Assert.True(security[0].TryGetProperty("bearerAuth", out _));

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearerAuth");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    [Fact]
    public void Authorize_AddsUnauthorizedResponse()
    {
        var options = new OpenApiOptions();
        options.AddJwtBearer();

        var document = GenerateDocument(options, Route("DELETE", "/products/{id}", typeof(DeleteProductEndpoint)));

        Assert.True(Operation(document, "/products/{id}", "delete")
            .GetProperty("responses").TryGetProperty("401", out _));
    }

    [Fact]
    public void WithoutRegisteredSchemes_NoSecurityIsEmitted()
    {
        var document = GenerateDocument(Route("DELETE", "/products/{id}", typeof(DeleteProductEndpoint)));
        Assert.False(Operation(document, "/products/{id}", "delete").TryGetProperty("security", out _));
    }

    [Fact]
    public void ApiKeyScheme_MatchesTheHandlerDefaultHeader()
    {
        var options = new OpenApiOptions();
        options.AddApiKey();

        var document = GenerateDocument(options, Route("GET", "/products", typeof(ListProductsEndpoint)));
        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("apiKey");

        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("X-API-Key", scheme.GetProperty("name").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
    }

    // ── Handler ──

    [Fact]
    public void Handler_ServesTheDocumentAtTheConfiguredPath()
    {
        var handler = new OpenApiHandler(new OpenApiOptions(), new[] { Route("GET", "/products", typeof(ListProductsEndpoint)) }, CamelCase);
        var response = new HttpResponse();

        Assert.True(handler.TryServe(new HttpRequest { Method = "GET", Path = "/openapi/v1.json" }, response));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(MediaTypes.ApplicationJson, response.ContentType);
        Assert.NotNull(response.Body);
        Assert.True(response.Headers.ContainsKey(HeaderNames.ETag));
    }

    [Fact]
    public void Handler_FallsThroughForOtherPaths()
    {
        var handler = new OpenApiHandler(new OpenApiOptions(), Array.Empty<RouteDescriptor>(), CamelCase);
        Assert.False(handler.TryServe(new HttpRequest { Method = "GET", Path = "/api/users" }, new HttpResponse()));
    }

    [Fact]
    public void Handler_IgnoresQueryStringWhenMatching()
    {
        var handler = new OpenApiHandler(new OpenApiOptions(), Array.Empty<RouteDescriptor>(), CamelCase);
        Assert.True(handler.TryServe(new HttpRequest { Method = "GET", Path = "/swagger?urls.primaryName=v1" }, new HttpResponse()));
    }

    [Fact]
    public void Handler_RevalidatesWithIfNoneMatch()
    {
        var handler = new OpenApiHandler(new OpenApiOptions(), Array.Empty<RouteDescriptor>(), CamelCase);

        var first = new HttpResponse();
        handler.TryServe(new HttpRequest { Method = "GET", Path = "/openapi/v1.json" }, first);
        var etag = first.Headers[HeaderNames.ETag];

        var request = new HttpRequest { Method = "GET", Path = "/openapi/v1.json" };
        request.Headers[HeaderNames.IfNoneMatch] = etag;
        var second = new HttpResponse();

        Assert.True(handler.TryServe(request, second));
        Assert.Equal(304, second.StatusCode);
        Assert.Null(second.Body);
    }

    [Fact]
    public void Handler_ServesTheUiPage()
    {
        var handler = new OpenApiHandler(new OpenApiOptions { Title = "My API" }, Array.Empty<RouteDescriptor>(), CamelCase);
        var response = new HttpResponse();

        Assert.True(handler.TryServe(new HttpRequest { Method = "GET", Path = "/swagger" }, response));
        Assert.Equal(MediaTypes.TextHtml, response.ContentType);

        var html = System.Text.Encoding.UTF8.GetString(response.Body!);
        Assert.Contains("swagger-ui", html, StringComparison.Ordinal);
        Assert.Contains("/openapi/v1.json", html, StringComparison.Ordinal);
        Assert.Contains("My API", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Handler_UiCanBeDisabled()
    {
        var handler = new OpenApiHandler(new OpenApiOptions { UiEnabled = false }, Array.Empty<RouteDescriptor>(), CamelCase);
        Assert.False(handler.TryServe(new HttpRequest { Method = "GET", Path = "/swagger" }, new HttpResponse()));
    }

    [Fact]
    public void Handler_HonorsCustomPaths()
    {
        var options = new OpenApiOptions { DocumentPath = "/docs/spec.json", UiPath = "/docs" };
        var handler = new OpenApiHandler(options, Array.Empty<RouteDescriptor>(), CamelCase);

        Assert.True(handler.TryServe(new HttpRequest { Method = "GET", Path = "/docs/spec.json" }, new HttpResponse()));
        Assert.True(handler.TryServe(new HttpRequest { Method = "GET", Path = "/docs" }, new HttpResponse()));
        Assert.False(handler.TryServe(new HttpRequest { Method = "GET", Path = "/openapi/v1.json" }, new HttpResponse()));
    }

    [Fact]
    public void Handler_UiPointsAtTheConfiguredCdn()
    {
        var options = new OpenApiOptions { SwaggerUiCdnBase = "/static/swagger-ui" };
        var handler = new OpenApiHandler(options, Array.Empty<RouteDescriptor>(), CamelCase);
        var response = new HttpResponse();

        handler.TryServe(new HttpRequest { Method = "GET", Path = "/swagger" }, response);
        var html = System.Text.Encoding.UTF8.GetString(response.Body!);

        Assert.Contains("/static/swagger-ui/swagger-ui.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unpkg.com", html, StringComparison.Ordinal);
    }

    // ── Router integration ──

    [Fact]
    public void Router_ExposesRegisteredRoutesAfterFreeze()
    {
        var router = new Router();
        router.AddEndpointType("GET", "/products", typeof(ListProductsEndpoint), EndpointInvoker.Build(typeof(ListProductsEndpoint)));
        router.AddEndpointType("POST", "/products", typeof(CreateProductEndpoint), EndpointInvoker.Build(typeof(CreateProductEndpoint)));
        router.Freeze();

        var routes = router.GetRegisteredRoutes();

        Assert.Equal(2, routes.Count);
        Assert.Contains(routes, r => r.Method == "GET" && r.Pattern == "/products");
        Assert.Contains(routes, r => r.Method == "POST" && r.Pattern == "/products");
        Assert.All(routes, r => Assert.NotNull(r.Invoker));
    }

    [Fact]
    public void Router_ThrowsWhenEnumeratedBeforeFreeze()
    {
        var router = new Router();
        Assert.Throws<InvalidOperationException>(() => router.GetRegisteredRoutes());
    }

    [Fact]
    public void Router_DoesNotExposeWebSocketRoutes()
    {
        var router = new Router();
        router.AddEndpointType("GET", "/products", typeof(ListProductsEndpoint), EndpointInvoker.Build(typeof(ListProductsEndpoint)));
        router.AddWebSocketRoute("/ws/echo", (_, _) => Task.CompletedTask);
        router.Freeze();

        Assert.Single(router.GetRegisteredRoutes());
    }

    [Fact]
    public void EndpointInvoker_ExposesResponseType()
    {
        var invoker = EndpointInvoker.Build(typeof(GetProductEndpoint));

        Assert.Equal(typeof(GetProductRequest), invoker.RequestType);
        Assert.Equal(typeof(Product), invoker.ResponseType);
    }
}
