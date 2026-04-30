using FluentAssertions;
using OpenMono.Tui;

namespace OpenMono.Tests.Tui;

public class StreamingMetricsTests
{
    [Fact]
    public void InitialState_IsNotStreaming()
    {
        var m = new StreamingMetrics();
        m.IsStreaming.Should().BeFalse();
        m.TokensPerSecond.Should().Be(0);
        m.TotalCompletionTokens.Should().Be(0);
    }

    [Fact]
    public void OnStreamStart_ResetsState()
    {
        var m = new StreamingMetrics();

        m.OnStreamStart();
        m.OnTokenReceived(50);
        m.OnStreamEnd();

        m.OnStreamStart();
        m.IsStreaming.Should().BeTrue();
        m.TotalCompletionTokens.Should().Be(0);
        m.TokensPerSecond.Should().Be(0);
    }

    [Fact]
    public void OnTokenReceived_TracksTotal()
    {
        var m = new StreamingMetrics();
        m.OnStreamStart();

        m.OnTokenReceived(10);
        m.TotalCompletionTokens.Should().Be(10);

        m.OnTokenReceived(25);
        m.TotalCompletionTokens.Should().Be(25);
    }

    [Fact]
    public void OnTokenReceived_CalculatesTokensPerSecond()
    {
        var m = new StreamingMetrics();
        m.OnStreamStart();

        m.OnTokenReceived(10);
        Thread.Sleep(50);
        m.OnTokenReceived(20);

        m.TokensPerSecond.Should().BeGreaterThan(0,
            "tokens/sec should be positive after receiving tokens over time");
    }

    [Fact]
    public void OnStreamEnd_StopsStreaming()
    {
        var m = new StreamingMetrics();
        m.OnStreamStart();
        m.OnTokenReceived(10);
        m.OnStreamEnd();

        m.IsStreaming.Should().BeFalse();
        m.TotalCompletionTokens.Should().Be(10);
    }

    [Fact]
    public void MultipleStreams_ResetBetween()
    {
        var m = new StreamingMetrics();

        m.OnStreamStart();
        m.OnTokenReceived(100);
        m.OnStreamEnd();
        m.TotalCompletionTokens.Should().Be(100);

        m.OnStreamStart();
        m.TotalCompletionTokens.Should().Be(0);
        m.OnTokenReceived(5);
        m.TotalCompletionTokens.Should().Be(5);
        m.OnStreamEnd();
    }
}
