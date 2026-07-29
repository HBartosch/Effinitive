using System.Globalization;

namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// A token-bucket rate limit: <see cref="PermitLimit"/> requests may be spent as a burst, and the bucket
/// refills completely over <see cref="Window"/>.
/// <para>
/// So <c>PermitLimit = 100, Window = 1 minute</c> means a client may burst 100 requests immediately and
/// thereafter sustain roughly 1.67 per second. This is what makes a token bucket friendlier than a fixed
/// window for real traffic: legitimate clients that idle then burst are not punished, while a sustained
/// flood is still clamped to the refill rate.
/// </para>
/// </summary>
public sealed class RateLimitPolicy
{
    /// <summary>Bucket capacity — the largest burst a client may spend at once.</summary>
    public int PermitLimit { get; }

    /// <summary>Time taken to refill the bucket from empty to <see cref="PermitLimit"/>.</summary>
    public TimeSpan Window { get; }

    /// <summary>Tokens replenished per second.</summary>
    public double TokensPerSecond { get; }

    /// <summary>Stable name used in cache keys so different policies never share a bucket.</summary>
    public string Name { get; }

    public RateLimitPolicy(int permitLimit, TimeSpan window, string name = "global")
    {
        if (permitLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(permitLimit), "Permit limit must be positive.");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");

        PermitLimit = permitLimit;
        Window = window;
        Name = name;
        TokensPerSecond = permitLimit / window.TotalSeconds;
    }

    /// <summary>
    /// Seconds a client must wait for one token to become available, rounded up so a
    /// <c>Retry-After</c> built from it never tells the caller to retry too early.
    /// </summary>
    public int SecondsUntilNextToken(double currentTokens)
    {
        if (currentTokens >= 1)
            return 0;

        var needed = 1 - currentTokens;
        var seconds = needed / TokensPerSecond;
        return Math.Max(1, (int)Math.Ceiling(seconds));
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Name}: {PermitLimit} per {Window.TotalSeconds}s");
}
