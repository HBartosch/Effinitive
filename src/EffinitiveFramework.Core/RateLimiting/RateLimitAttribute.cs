namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// Gives an endpoint its own rate limit instead of the server-wide default.
/// <para>
/// The endpoint limit is an <i>additional</i> allowance rather than a replacement: the global policy
/// still applies, so an endpoint limit can tighten protection but never remove it. Use
/// <see cref="DisableRateLimitAttribute"/> to exempt an endpoint entirely.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // Expensive endpoint: 5 requests per minute per client, on top of the global limit.
/// [RateLimit(PermitLimit = 5, WindowSeconds = 60)]
/// public class GenerateReportEndpoint : NoRequestAsyncEndpointBase&lt;Report&gt; { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RateLimitAttribute : Attribute
{
    /// <summary>Burst capacity — requests allowed at once before throttling begins.</summary>
    public int PermitLimit { get; set; } = 60;

    /// <summary>Seconds taken to refill the allowance from empty.</summary>
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Exempts an endpoint from rate limiting entirely, including the global policy.
/// <para>
/// Intended for endpoints that must answer even while a client is being throttled — health checks
/// polled by a load balancer, readiness probes, metrics scrapes.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DisableRateLimitAttribute : Attribute
{
}
