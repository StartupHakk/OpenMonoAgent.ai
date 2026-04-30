using System.Reflection;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tests.Fakes;

namespace OpenMono.Tests.Rendering;

public sealed class AnsiInputReaderTests
{

    private static (AnsiInputReader reader, TerminalMockWrapper terminal) BuildReader()
    {
        var terminal  = new TerminalMockWrapper();
        var config    = new AppConfig();
        var session   = new SessionState();
        var painter   = new AnsiPainter(config, session, terminal);
        var overlay   = new AnsiSuggestionOverlay(config, painter);
        var reader    = new AnsiInputReader(painter, overlay, terminal);
        painter.SetBgInputProvider(() => reader.BgInputText);
        return (reader, terminal);
    }

    [Fact]
    public async Task ReadInput_ReturnsTypedLine()
    {
        var (reader, terminal) = BuildReader();

        terminal.QueueChar('H', ConsoleKey.H);
        terminal.QueueChar('i', ConsoleKey.I);
        terminal.QueueEnter();

        var result = await Task.Run(() => reader.ReadInput());
        result.Should().Be("Hi");
    }

    [Fact]
    public async Task ReadInput_HandlesBackspace()
    {
        var (reader, terminal) = BuildReader();

        terminal.QueueChar('A', ConsoleKey.A);
        terminal.QueueChar('\b', ConsoleKey.Backspace);
        terminal.QueueChar('B', ConsoleKey.B);
        terminal.QueueEnter();

        var result = await Task.Run(() => reader.ReadInput());
        result.Should().Be("B");
    }

    [Fact]
    public async Task ReadInput_HandlesDelete()
    {
        var (reader, terminal) = BuildReader();

        terminal.QueueChar('A', ConsoleKey.A);
        terminal.QueueChar('B', ConsoleKey.B);

        terminal.QueueChar('\0', ConsoleKey.LeftArrow);
        terminal.QueueChar('\0', ConsoleKey.Delete);
        terminal.QueueEnter();

        var result = await Task.Run(() => reader.ReadInput());
        result.Should().Be("A");
    }

    [Fact]
    public async Task ReadInput_CtrlU_KillsLine()
    {
        var (reader, terminal) = BuildReader();

        terminal.QueueChar('H', ConsoleKey.H);
        terminal.QueueChar('i', ConsoleKey.I);
        terminal.QueueChar('\x15', ConsoleKey.U, ctrl: true);
        terminal.QueueChar('X', ConsoleKey.X);
        terminal.QueueEnter();

        var result = await Task.Run(() => reader.ReadInput());
        result.Should().Be("X");
    }

    [Fact]
    public async Task ReadInput_CtrlW_DeletesLastWord()
    {
        var (reader, terminal) = BuildReader();

        foreach (var c in "hello world")
            terminal.QueueChar(c, ConsoleKey.NoName);
        terminal.QueueChar('\x17', ConsoleKey.W, ctrl: true);
        terminal.QueueEnter();

        var result = await Task.Run(() => reader.ReadInput());
        result.Should().Be("hello ");
    }

    [Fact(Timeout = 5_000)]
    public async Task BurstInput_DoesNotDeadlock()
    {
        var (reader, terminal) = BuildReader();

        terminal.QueueBurst(50, 'x', ConsoleKey.X);
        terminal.QueueEnter();

        var result = await Task.Run(() => reader.ReadInput());
        result.Should().HaveLength(50).And.Match(s => s.All(c => c == 'x'));
    }

    [Fact]
    public void SafeExit_WritesRestoreSequence_WhenInFullScreen()
    {
        var terminal = new TerminalMockWrapper();
        var renderer = new AnsiTuiRenderer(new AppConfig(), new SessionState(), terminal);

        renderer.EnterFullScreen();
        renderer.SafeExit();

        terminal.RawOutput.Should().Contain("\x1b[?1049l",
            "alt-screen restore sequence must appear in output");
        terminal.RawOutput.Should().Contain("\x1b[?25h",
            "cursor show sequence must appear in output");
    }

    [Fact]
    public void SafeExit_IsIdempotent_WhenCalledTwice()
    {
        var terminal = new TerminalMockWrapper();
        var renderer = new AnsiTuiRenderer(new AppConfig(), new SessionState(), terminal);

        renderer.EnterFullScreen();
        renderer.SafeExit();
        terminal.ClearOutput();

        renderer.SafeExit();

        terminal.RawOutput.Should().BeEmpty("SafeExit must not write when already exited");
    }

    [Fact]
    public void SignalInterrupt_TriggersSafeExit()
    {
        var terminal = new TerminalMockWrapper();
        var renderer = new AnsiTuiRenderer(new AppConfig(), new SessionState(), terminal);

        renderer.EnterFullScreen();
        terminal.SignalInterrupt();

        terminal.RawOutput.Should().Contain("\x1b[?1049l",
            "interrupt signal must restore the terminal");
    }

    [Fact]
    public void IRenderer_IsZeroMemberComposite()
    {
        typeof(IRenderer)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().BeEmpty("IRenderer is a composite alias — it must not declare new members");
    }

    [Fact]
    public void AnsiTuiRenderer_ImplementsAllThreeSubInterfaces()
    {
        typeof(AnsiTuiRenderer).Should().Implement<IOutputSink>();
        typeof(AnsiTuiRenderer).Should().Implement<IInputReader>();
        typeof(AnsiTuiRenderer).Should().Implement<ILiveFeedback>();
    }
}
