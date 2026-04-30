using FluentAssertions;
using OpenMono.Tests.Fakes;

namespace OpenMono.Tests.Fakes;

public sealed class TerminalMockWrapperTests
{
    [Fact]
    public async Task WriteAsync_CapturesOutput()
    {
        var t = new TerminalMockWrapper();
        await t.WriteAsync("hello");
        t.PlainOutput.Should().Be("hello");
    }

    [Fact]
    public async Task ReadKeyAsync_ReturnsQueuedKey()
    {
        var t = new TerminalMockWrapper();
        t.QueueEnter();
        var key = await t.ReadKeyAsync();
        key.Key.Should().Be(ConsoleKey.Enter);
    }

    [Fact]
    public void SignalInterrupt_FiresInterruptRequested()
    {
        var t = new TerminalMockWrapper();
        ConsoleSpecialKey? received = null;
        t.InterruptRequested += k => received = k;

        t.SignalInterrupt();

        received.Should().Be(ConsoleSpecialKey.ControlC);
    }

    [Fact]
    public void SimulateResize_UpdatesDimensions()
    {
        var t = new TerminalMockWrapper();
        t.SimulateResize(80, 24);
        t.WindowWidth.Should().Be(80);
        t.WindowHeight.Should().Be(24);
    }

    [Fact]
    public void TryReadKey_ReturnsNull_WhenQueueEmpty()
    {
        var t = new TerminalMockWrapper();
        t.TryReadKey().Should().BeNull();
    }
}
