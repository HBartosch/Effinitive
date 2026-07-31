# Rate Limiting in EffinitiveFramework

`UseRateLimiting()` gives every client a token-bucket allowance, keyed by IP address. Callers may burst
up to a configured limit and then sustain the refill rate; anything over gets `429 Too Many Requests`
with `Retry-After`.

It works identically across HTTP/1.1, HTTP/2 and HTTP/3, and applies before routing — so static files,
the OpenAPI document, and floods aimed at paths that do not exist are all covered.

## Enabling it

```csharp
var app = EffinitiveApp.Create()
    .UseRateLimiting(limits =>
    {
        limits.PermitLimit = 100;                  // burst capacity
        limits.Window = TimeSpan.FromMinutes(1);   // time to refill from empty
    })
    .MapEndpoints()
    .Build();
```

Those defaults let a client fire 100 requests immediately and then sustain about 1.7 per second.

## Why a token bucket

A fixed window ("100 requests per minute") lets a client send 100 at 11:59:59 and another 100 at
12:00:00 — a 200-request burst straddling the boundary. A token bucket has no boundary: the allowance
refills continuously, so a client that idles and then bursts is not punished, while a sustained flood is
clamped to the refill rate.

Each client costs two numbers — a token count and a timestamp — and refill is computed on access rather
than by a timer, so idle clients cost nothing and there is no background work proportional to the number
of clients being tracked.

## Per-endpoint limits

```csharp
// Expensive: 5 per minute per client, on top of the server-wide limit.
[RateLimit(PermitLimit = 5, WindowSeconds = 60)]
public class GenerateReportEndpoint : NoRequestAsyncEndpointBase<Report> { }

// Never throttled — a load balancer must always get an answer.
[DisableRateLimit]
public class HealthCheckEndpoint : NoRequestEndpointBase<HealthResponse> { }
```

An endpoint limit is an **additional** allowance, not a replacement: the global limit still applies, so
`[RateLimit]` can only tighten protection. `[DisableRateLimit]` exempts an endpoint from everything,
including the global policy — which is why exemption is checked before the global limit is spent rather
than after.

## Behind a reverse proxy

Behind nginx, CloudFlare, or a load balancer, every request arrives from the proxy's address. Left
alone, the limiter would treat all of your traffic as one client and throttle everybody at once.

```csharp
.UseRateLimiting(limits =>
{
    limits.PermitLimit = 100;
    limits.AddTrustedProxy("10.0.0.1");   // enables forwarded headers for this peer only
})
```

`X-Forwarded-For` is **ignored by default, deliberately**. The header is supplied by the caller, so
trusting it unconditionally would let anyone mint a fresh allowance on every request simply by varying
the value — making the limiter worse than useless. Three rules follow from that:

- The header is only read when the connection actually came from an address in `TrustedProxies`.
- The chain is walked **right to left**, skipping entries that are themselves trusted proxies. The
  rightmost entries were appended by your own infrastructure and reflect peers it really saw; the
  leftmost is whatever the original caller sent and can be forged.
- Setting `TrustForwardedHeaders = true` without listing any proxy trusts nobody. A trusted-proxy setup
  with no trusted proxies is a misconfiguration, not permission to believe every caller.

## What a rejected request looks like

```
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 1
X-RateLimit-Limit: 20
X-RateLimit-Remaining: 0
X-RateLimit-Reset: 1

{"type":"...","title":"Too Many Requests","status":429,"detail":"Rate limit exceeded. Retry in 1 second(s)."}
```

`Retry-After` is whole seconds until the next permit, rounded up so a client following it never retries
too early. The body is `ProblemDetails`, matching how the server reports every other error.

Set `EmitRateLimitHeaders = false` to drop the `X-RateLimit-*` headers if you would rather not publish
how the limiter is configured; `Retry-After` is standard and always sent.

> **Headers on successful responses are not emitted.** The limiter runs before the endpoint, and the
> request pipeline resets the response object before invoking the handler, so anything stamped early
> would be wiped. Only rejections carry rate-limit headers.

## Memory is bounded

`MaxTrackedClients` (default 100,000) caps how many allowances are held at once. This is not a tuning
knob so much as a safety property: a dictionary that grows an entry per distinct source address would
itself be the denial-of-service vector rate limiting exists to prevent.

When the cap is reached, the store first drops allowances that have been idle for a full window. Those
have refilled to capacity, which is exactly the state a new allowance starts in, so dropping them loses
nothing. Only if that is not enough does it evict the least recently seen clients.

## Options

| Option | Default | Meaning |
|---|---|---|
| `PermitLimit` | 100 | Burst capacity of the server-wide limit |
| `Window` | 1 minute | Time to refill the allowance from empty |
| `MaxTrackedClients` | 100,000 | Cap on tracked allowances |
| `RejectionStatusCode` | 429 | Status returned when over the limit |
| `EmitRateLimitHeaders` | `true` | Whether rejections carry `X-RateLimit-*` |
| `TrustForwardedHeaders` | `false` | Whether `X-Forwarded-For` is consulted at all |
| `TrustedProxies` | empty | Peers whose forwarded header is believed |
| `Store` | `MemoryRateLimitStore` | Backing store; assign your own `IRateLimitStore` to share limits across a cluster |

Capture the options to read diagnostics after startup:

```csharp
RateLimitOptions? limits = null;
var app = EffinitiveApp.Create()
    .UseRateLimiting(o => limits = o)
    .MapEndpoints()
    .Build();

Console.WriteLine($"tracking {limits!.Store!.TrackedClients} clients");
```

## Not covered

Fixed-window, sliding-window and concurrency limiters; distributed stores out of the box; partitioning
by user or API key; queuing rejected requests rather than failing them.

## Try it

The sample app limits to 20 requests per 30 seconds:

```bash
for i in $(seq 1 25); do curl -s -o /dev/null -w "%{http_code} " http://localhost:5000/api/plain; done
```

Expect a run of `200`s followed by `429`s. `/api/health` keeps answering throughout because it carries
`[DisableRateLimit]`, and `/api/report` trips at 5 because of its own `[RateLimit]`.
