namespace OpenMono.Rendering;

internal sealed class ConsoleTerminal : ITerminal
{
    public int WindowWidth        => Console.WindowWidth;
    public int WindowHeight       => Console.WindowHeight;
    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public event Action<ConsoleSpecialKey>? InterruptRequested;

    public ConsoleTerminal()
    {

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            InterruptRequested?.Invoke(e.SpecialKey);
        };
    }

    public ValueTask WriteAsync(string value, CancellationToken ct = default)
    {
        Console.Write(value);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteLineAsync(string value, CancellationToken ct = default)
    {
        Console.WriteLine(value);
        return ValueTask.CompletedTask;
    }

    public ConsoleKeyInfo? TryReadKey() =>
        Console.KeyAvailable ? Console.ReadKey(intercept: true) : null;

    public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Console.ReadKey(intercept: true));
}
