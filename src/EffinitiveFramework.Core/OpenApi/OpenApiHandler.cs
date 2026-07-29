using System.Text;
using System.Text.Json;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core.OpenApi;

/// <summary>
/// Serves the generated OpenAPI document and, optionally, an HTML page that renders it.
/// <para>
/// The document is generated and serialized once in the constructor, so a request is a path comparison
/// and a write of a pre-built buffer — the same shape as
/// <see cref="StaticFiles.StaticFileHandler"/>'s fast path, and with the same fall-through contract:
/// return false and the request continues to routing.
/// </para>
/// </summary>
public sealed class OpenApiHandler
{
    private readonly string _documentPath;
    private readonly string? _uiPath;
    private readonly byte[] _documentBytes;
    private readonly string _documentETag;
    private readonly byte[]? _uiBytes;

    internal OpenApiHandler(OpenApiOptions options, IReadOnlyList<RouteDescriptor> routes, JsonSerializerOptions? jsonOptions)
    {
        _documentPath = NormalizePath(options.DocumentPath);
        _uiPath = options.UiEnabled ? NormalizePath(options.UiPath) : null;

        var generator = new OpenApiDocumentGenerator(options, jsonOptions);
        var document = generator.Generate(routes);

        _documentBytes = JsonSerializer.SerializeToUtf8Bytes(document, OpenApiDocumentObject.SerializerOptions);
        _documentETag = EffinitiveServer.ComputeBodyETag(_documentBytes);

        if (_uiPath != null)
            _uiBytes = Encoding.UTF8.GetBytes(BuildUiHtml(options, _documentPath));
    }

    /// <summary>The serialized document, exposed for diagnostics and tests.</summary>
    public ReadOnlyMemory<byte> DocumentBytes => _documentBytes;

    /// <summary>
    /// Serves the document or UI page if the request targets one. Returns false to let the request
    /// fall through to routing. Only GET and HEAD should be dispatched here.
    /// </summary>
    public bool TryServe(HttpRequest request, HttpResponse response)
    {
        var path = request.Path.AsSpan();

        // Strip the query string — the UI appends its own parameters when deep-linking.
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
            path = path[..queryIndex];

        if (path.Equals(_documentPath, StringComparison.OrdinalIgnoreCase))
        {
            WriteDocument(request, response);
            return true;
        }

        if (_uiPath != null && _uiBytes != null && path.Equals(_uiPath, StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 200;
            response.ContentType = MediaTypes.TextHtml;
            response.Body = _uiBytes;
            // The page is a thin shell around the document; let it revalidate rather than stick.
            response.Headers[HeaderNames.CacheControl] = HeaderValues.NoCache;
            return true;
        }

        return false;
    }

    private void WriteDocument(HttpRequest request, HttpResponse response)
    {
        response.Headers[HeaderNames.ETag] = _documentETag;
        response.Headers[HeaderNames.CacheControl] = HeaderValues.NoCache;

        // The document only changes when the app restarts, so revalidation is cheap and always correct.
        if (request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch) &&
            EffinitiveServer.WeakETagMatch(ifNoneMatch, _documentETag))
        {
            response.StatusCode = 304;
            response.Body = null;
            return;
        }

        response.StatusCode = 200;
        response.ContentType = MediaTypes.ApplicationJson;
        response.Body = _documentBytes;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
            trimmed = "/" + trimmed;
        // Keep "/" itself intact, otherwise drop a trailing slash so both forms match.
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    /// <summary>
    /// Minimal Swagger UI shell. The assets come from <see cref="OpenApiOptions.SwaggerUiCdnBase"/>,
    /// which defaults to a public CDN — repoint it at a self-hosted copy of swagger-ui-dist to render
    /// without internet access.
    /// </summary>
    private static string BuildUiHtml(OpenApiOptions options, string documentPath)
    {
        var cdn = options.SwaggerUiCdnBase.TrimEnd('/');
        var title = HtmlEncode(options.Title);

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{title}}</title>
          <link rel="stylesheet" href="{{cdn}}/swagger-ui.css" />
          <style>
            body { margin: 0; background: #fafafa; }
            .swagger-ui .topbar { display: none; }
          </style>
        </head>
        <body>
          <div id="swagger-ui"></div>
          <script src="{{cdn}}/swagger-ui-bundle.js" crossorigin></script>
          <script>
            window.onload = function () {
              window.ui = SwaggerUIBundle({
                url: '{{documentPath}}',
                dom_id: '#swagger-ui',
                deepLinking: true,
                displayRequestDuration: true,
                tryItOutEnabled: true
              });
            };
          </script>
        </body>
        </html>
        """;
    }

    private static string HtmlEncode(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal);
}
