using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using OpenMono.Rendering;

namespace OpenMono.Tests.Fakes;

public sealed class TerminalMockWrapper : ITerminal
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _output = new();
    private readonly Channel<ConsoleKeyInfo> _keys =
        Channel.CreateUnbounded<ConsoleKeyInfo>(
            new UnboundedChannelOptions { SingleReader = true });

    public int  WindowWidth  { get; set; } = 120;
    public int  WindowHeight { get; set; } = 40;
    public bool IsOutputRedirected => true;

    public event Action<ConsoleSpecialKey>? InterruptRequested;

    public string RawOutput
    {
        get { lock (_gate) { return _output.ToString(); } }
    }

    public string PlainOutput =>
        Regex.Replace(RawOutput, @"\x1b\[[0-9;]*[a-zA-Z]", "");

    public ValueTask WriteAsync(string value, CancellationToken ct = default)
    {
        lock (_gate) { _output.Append(value); }
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteLineAsync(string value, CancellationToken ct = default) =>
        WriteAsync(value + Environment.NewLine, ct);

    public ConsoleKeyInfo? TryReadKey() =>
        _keys.Reader.TryRead(out var k) ? k : null;

    public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default) =>
        await _keys.Reader.ReadAsync(ct);

    public void QueueChar(char c, ConsoleKey key = ConsoleKey.NoName,
        bool shift = false, bool alt = false, bool ctrl = false) =>
        _keys.Writer.TryWrite(new ConsoleKeyInfo(c, key, shift, alt, ctrl));

    public void QueueEnter()  => QueueChar('\n',  ConsoleKey.Enter);
    public void QueueEscape() => QueueChar('\0',  ConsoleKey.Escape);
    public void QueueCtrlC()  => QueueChar('\x03', ConsoleKey.C, ctrl: true);

    public void QueueBurst(int count, char c = 'a', ConsoleKey key = ConsoleKey.A)
    {
        for (var i = 0; i < count; i++)
            QueueChar(c, key);
    }

    public void SimulateResize(int width, int height)
    {
        WindowWidth  = width;
        WindowHeight = height;
    }

    public void SignalInterrupt(ConsoleSpecialKey key = ConsoleSpecialKey.ControlC) =>
        InterruptRequested?.Invoke(key);

    public bool ContainsAnsi(string fragment) => RawOutput.Contains(fragment);

    public void ClearOutput()
    {
        lock (_gate) { _output.Clear(); }
    }
}
