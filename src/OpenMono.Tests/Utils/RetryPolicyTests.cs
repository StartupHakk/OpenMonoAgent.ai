using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using OpenMono.Utils;

namespace OpenMono.Tests.Utils;

public class RetryPolicyTests
{
    [Fact]
    public void NextDelay_FullJitter_StaysWithinExponentialBase()
    {
        // attempt 1 -> base 1s. Full jitter scales [0, base] by the random fraction.
        RetryPolicy.NextDelay(1, retryAfter: null, jitterFraction: 0.0).Should().Be(TimeSpan.Zero);
        RetryPolicy.NextDelay(1, retryAfter: null, jitterFraction: 1.0).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
        RetryPolicy.NextDelay(1, retryAfter: null, jitterFraction: 0.5).Should().BeGreaterThan(TimeSpan.Zero);
        RetryPolicy.NextDelay(1, retryAfter: null, jitterFraction: 0.5).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void NextDelay_LaterAttempts_BackOffFurther()
    {
        var early = RetryPolicy.NextDelay(1, retryAfter: null, jitterFraction: 1.0);
        var later = RetryPolicy.NextDelay(3, retryAfter: null, jitterFraction: 1.0);
        later.Should().BeGreaterThan(early);
    }

    [Fact]
    public void NextDelay_HonorsRetryAfter_NeverShorterThanServerValue()
    {
        var delay = RetryPolicy.NextDelay(1, retryAfter: TimeSpan.FromSeconds(10), jitterFraction: 0.0);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ParseRetryAfter_ReadsDeltaSeconds()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        RetryPolicy.ParseRetryAfter(resp).Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void ParseRetryAfter_ReturnsNull_WhenHeaderAbsent()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK);
        RetryPolicy.ParseRetryAfter(resp).Should().BeNull();
    }
}
