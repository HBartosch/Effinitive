# Response Caching in EffinitiveFramework

EffinitiveFramework can serve repeat `GET` / `HEAD` requests straight from an in-process store, skipping
endpoint execution, DI scope creation, and JSON serialization entirely. The same middleware emits the
`Cache-Control`, `Vary` and `Age` headers that let browser and proxy caches participate.

Caching is **opt-in per endpoint**. Adding the middleware to an existing app changes nothing until an
endpoint is marked with `[ResponseCache]`, so there is no way to accidentally start caching a response
that was never meant to be shared.

## Enabling it

```csharp
var app = EffinitiveApp.Create()
    .UseResponseCompression(minimumSize: 1024)   // register FIRST
    .UseResponseCaching(cache =>                 // then caching
    {
        cache.MaxCacheSizeBytes = 32 * 1024 * 1024;
    })
    .MapEndpoints()
    .Build();
```

> **Ordering matters.** The pipeline runs first-registered outermost, so `UseResponseCaching()` must come
> **after** `UseResponseCompression()`. The cache then stores uncompressed bytes and every hit returns
> back out through the compression middleware, so hits are compressed exactly like misses. Registered the
> other way round, a hit short-circuits past compression and goes out uncompressed — `Build()` prints a
> warning if it detects that order.

## Marking an endpoint

```csharp
using EffinitiveFramework.Core.Caching;

[ResponseCache(Duration = 30)]
public class CachedReportEndpoint : NoRequestAsyncEndpointBase<ReportResponse>
{
    protected override string Method => "GET";
    protected override string Route => "/api/report";

    public override async Task<ReportResponse> HandleAsync(CancellationToken ct = default)
    {
        await Task.Delay(250, ct);           // expensive work — runs at most once per 30s
        return new ReportResponse { GeneratedAt = DateTime.UtcNow };
    }
}
```

```
$ curl -i http://localhost:5000/api/report      # 250 ms — endpoint ran
Cache-Control: public, max-age=30
ETag: "7ac08c912666f0cc"

$ curl -i http://localhost:5000/api/report      # instant — served from cache
Cache-Control: public, max-age=30
ETag: "7ac08c912666f0cc"
Age: 4
```

### `[ResponseCache]` reference

| Property | Default | Meaning |
|---|---|---|
| `Duration` | `ResponseCacheOptions.DefaultDuration` (60s) | Freshness lifetime, in seconds. |
| `Location` | `Any` | `Any` → `public, max-age=N` and stored server-side. `Client` → `private, max-age=N`, headers only. `None` → `no-cache, no-store`. |
| `NoStore` | `false` | Emits `no-store` and disables storage. |
| `AllowAuthenticated` | `false` | Permits requests carrying `Authorization` to use the shared cache — see below. |
| `VaryByHeader` | none | Comma-separated request headers that select the representation. Added to the cache key and emitted as `Vary`. |
| `VaryByQueryKeys` | none | Comma-separated query keys that select the representation. When set, only these participate in the key; otherwise the whole query string does. |

```csharp
// One entry per language, and ?utm=... no longer fragments the cache.
[ResponseCache(Duration = 300, VaryByHeader = "Accept-Language", VaryByQueryKeys = "page,pageSize")]
public class GetArticlesEndpoint : ... { }
```

## Authenticated requests

By default a request carrying an `Authorization` header **bypasses the shared cache entirely** — it is
neither served from it nor stored in it. An endpoint that is both `[Authorize]` and cached would
otherwise key on the path alone and hand one user's response to the next caller.

Set `AllowAuthenticated = true` only when the response is identical for every caller regardless of who
they are (a public catalogue behind an API gateway that always attaches a token, for example).

## Invalidation

Entries leave the cache in three ways:

1. **Expiry** — the `Duration` elapses.
2. **A write to the same path** — a `POST` / `PUT` / `PATCH` / `DELETE` returning 2xx or 3xx evicts every
   cached representation of that path (RFC 9111 §4.4). A failed write invalidates nothing.
3. **Memory pressure** — when the store exceeds `MaxCacheSizeBytes` it drops expired entries first, then
   the oldest ones.

Invalidation is O(1): each path carries a generation counter that the cache key embeds, so a write
orphans every prior entry for that path at once rather than walking an index.

## Conditional requests

Cached entries carry a strong `ETag` derived from the body, so `If-None-Match` on a hit is answered with
`304 Not Modified` directly from the middleware — including on HTTP/2 and HTTP/3, where the HTTP/1.1
connection loop's conditional handling does not run.

## What is never cached

- Responses from endpoints without `[ResponseCache]`, and routes registered as plain delegates (there is
  no type to carry the attribute).
- Streaming responses — SSE and other `StreamHandler` endpoints own the connection.
- Stream-backed bodies. Static files are served before the middleware pipeline and have their own
  `ETag` / `Last-Modified` / `Range` handling in `StaticFileHandler`.
- Responses carrying `Set-Cookie`, or whose own `Cache-Control` says `no-store` or `private`. An endpoint
  that sets `Cache-Control` itself always wins over the attribute.
- Status codes outside `ResponseCacheOptions.CacheableStatusCodes` (200 by default).
- Bodies larger than `MaxBodySizeBytes` (1 MB by default), so one big payload can't evict everything else.
- Requests sending `Cache-Control: no-store`. `no-cache` (or `Pragma: no-cache`) forces a fresh response
  but the result still replaces the stored entry.

## Options and statistics

```csharp
ResponseCacheOptions? cacheOptions = null;

var app = EffinitiveApp.Create()
    .UseResponseCompression()
    .UseResponseCaching(o => cacheOptions = o)
    .MapEndpoints()
    .Build();

// After startup:
Console.WriteLine($"{cacheOptions!.Store!.Hits} hits / {cacheOptions.Store.Misses} misses, " +
                  $"{cacheOptions.Store.EntryCount} entries, {cacheOptions.Store.SizeBytes} bytes");
```

| Option | Default | Meaning |
|---|---|---|
| `MaxBodySizeBytes` | 1 MB | Largest response body that will be stored. |
| `MaxCacheSizeBytes` | 100 MB | Total memory budget for cached bodies. |
| `DefaultDuration` | 60s | Used when `[ResponseCache]` omits `Duration`. |
| `CacheableStatusCodes` | `{ 200 }` | Status codes eligible for storage. |
| `Store` | `MemoryResponseCache` | The backing store. Assign your own `IResponseCache` to replace it. |

## Try it

The sample app (`samples/EffinitiveFramework.Sample`) exposes `GET /api/report` with a 30-second cache
and `POST /api/report` to invalidate it:

```bash
curl -i http://localhost:5000/api/report     # slow, no Age header
curl -i http://localhost:5000/api/report     # instant, Age: N, identical body
curl -X POST http://localhost:5000/api/report
curl -i http://localhost:5000/api/report     # recomputed
```
