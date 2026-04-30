using FluentAssertions;
using OpenMono.Tui;

namespace OpenMono.Tests.Tui;

public class ContextWindowMeterTests
{
    [Fact]
    public void InitialState_IsEmpty()
    {
        var meter = new ContextWindowMeter(128_000);
        meter.PromptTokens.Should().Be(0);
        meter.UsagePercent.Should().Be(0);
        meter.RemainingTokens.Should().Be(128_000);
    }

    [Fact]
    public void Update_CalculatesPercent()
    {
        var meter = new ContextWindowMeter(100);
        meter.Update(25);
        meter.UsagePercent.Should().Be(25);
        meter.RemainingTokens.Should().Be(75);
    }

    [Fact]
    public void Update_ClampsTotalAtContextSize()
    {
        var meter = new ContextWindowMeter(100);
        meter.Update(150);
        meter.UsagePercent.Should().Be(150);
        meter.RemainingTokens.Should().Be(0);
    }

    [Fact]
    public void FormatRemaining_ThousandsFormat()
    {
        var meter = new ContextWindowMeter(128_000);
        meter.Update(0);
        meter.FormatRemaining().Should().Contain("K remaining");

        meter.Update(127_500);
        meter.FormatRemaining().Should().Be("500 remaining");
    }

    [Fact]
    public void FormatProgressBar_Empty()
    {
        var meter = new ContextWindowMeter(100);
        var bar = meter.FormatProgressBar(10);
        bar.Should().HaveLength(10);
        bar.Should().NotContain("\u2588");
    }

    [Fact]
    public void FormatProgressBar_HalfFull()
    {
        var meter = new ContextWindowMeter(100);
        meter.Update(50);
        var bar = meter.FormatProgressBar(10);
        bar.Should().HaveLength(10);
        bar.Should().StartWith("\u2588\u2588\u2588\u2588\u2588");
    }

    [Fact]
    public void FormatProgressBar_Full()
    {
        var meter = new ContextWindowMeter(100);
        meter.Update(100);
        var bar = meter.FormatProgressBar(10);
        bar.Should().Be(new string('\u2588', 10));
    }

    [Fact]
    public void DefaultContextSize_Is128K()
    {
        var meter = new ContextWindowMeter();
        meter.RemainingTokens.Should().Be(128_000);
    }

    [Fact]
    public void InvalidContextSize_FallsBackToDefault()
    {
        var meter = new ContextWindowMeter(-1);
        meter.RemainingTokens.Should().Be(128_000);
    }
}
