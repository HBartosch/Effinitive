namespace EffinitiveFramework.Core.RateLimiting;

/// <summary>
/// One client's allowance under one policy: a token count and the time it was last refilled.
/// <para>
/// Refill is lazy — computed from elapsed time on each access rather than driven by a timer — so a
/// million idle clients cost nothing beyond their dictionary entries, and there is no background work
/// proportional to the number of tracked clients.
/// </para>
/// <para>
/// Access is guarded by a per-bucket lock. Contention is therefore per client rather than global: two
/// requests from different clients never block each other, and two from the same client are ordered by
/// a lock held for a handful of instructions.
/// </para>
/// </summary>
internal sealed class TokenBucket
{
    private readonly RateLimitPolicy _policy;
    private readonly object _gate = new();

    private double _tokens;
    private long _lastRefillTicks;

    public TokenBucket(RateLimitPolicy policy, long nowTicks)
    {
        _policy = policy;
        _tokens = policy.PermitLimit;   // new clients start with a full burst allowance
        _lastRefillTicks = nowTicks;
    }

    /// <summary>Ticks at which this bucket was last touched, used to evict idle clients.</summary>
    public long LastAccessTicks => Interlocked.Read(ref _lastRefillTicks);

    /// <summary>
    /// Once a bucket has been idle for a full window it has refilled to capacity, which is exactly the
    /// state a freshly created bucket starts in — so evicting it past this point loses no information
    /// and cannot let a client exceed its limit.
    /// </summary>
    public bool IsDisposableAt(long nowTicks) => nowTicks - LastAccessTicks > _policy.Window.Ticks;

    /// <summary>
    /// Attempts to spend one token.
    /// </summary>
    /// <param name="nowTicks">Current UTC tick count, passed in so tests can control time.</param>
    /// <param name="retryAfterSeconds">When denied, whole seconds until a token is available.</param>
    /// <param name="remaining">Tokens left after the attempt, floored at zero.</param>
    public bool TryAcquire(long nowTicks, out int retryAfterSeconds, out int remaining)
    {
        lock (_gate)
        {
            Refill(nowTicks);

            if (_tokens >= 1)
            {
                _tokens -= 1;
                retryAfterSeconds = 0;
                remaining = (int)_tokens;
                return true;
            }

            retryAfterSeconds = _policy.SecondsUntilNextToken(_tokens);
            remaining = 0;
            return false;
        }
    }

    /// <summary>Adds the tokens earned since the last refill, capped at the bucket's capacity.</summary>
    private void Refill(long nowTicks)
    {
        var elapsedTicks = nowTicks - _lastRefillTicks;

        // A clock that went backwards (NTP correction) would otherwise subtract tokens.
        if (elapsedTicks <= 0)
        {
            _lastRefillTicks = nowTicks;
            return;
        }

        var elapsedSeconds = (double)elapsedTicks / TimeSpan.TicksPerSecond;
        _tokens = Math.Min(_policy.PermitLimit, _tokens + elapsedSeconds * _policy.TokensPerSecond);
        _lastRefillTicks = nowTicks;
    }
}
