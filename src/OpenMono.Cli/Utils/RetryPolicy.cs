namespace OpenMono.Utils;

/// <summary>
/// Shared backoff policy for LLM HTTP retries. Honors a server <c>Retry-After</c>
/// header when present, and otherwise applies full jitter over an exponential base
/// so that many concurrent callers (e.g. parallel sub-agents) don't all retry on the
/// same tick and stampede the endpoint.
/// </summary>
public static class RetryPolicy
{
    private static readonly TimeSpan[] BaseDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    ];

    /// <param name="attempt">1-based index of the upcoming retry.</param>
    /// <param name="retryAfter">Server-supplied Retry-After, if any.</param>
    /// <param name="jitterFraction">A random value in [0,1) (injected for testability).</param>
    public static TimeSpan NextDelay(int attempt, TimeSpan? retryAfter, double jitterFraction)
    {
        jitterFraction = Math.Clamp(jitterFraction, 0.0, 1.0);

        // The server told us how long to wait — honor it, plus a touch of jitter on top
        // so concurrent callers don't resume in lockstep. Never wait less than asked.
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return ra + TimeSpan.FromMilliseconds(1000 * jitterFraction);

        var baseDelay = BaseDelays[Math.Clamp(attempt - 1, 0, BaseDelays.Length - 1)];

        // Full jitter: uniform in [0, baseDelay].
        return baseDelay * jitterFraction;
    }

    public static TimeSpan? ParseRetryAfter(HttpResponseMessage? response)
    {
        var header = response?.Headers.RetryAfter;
        if (header is null) return null;

        if (header.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
