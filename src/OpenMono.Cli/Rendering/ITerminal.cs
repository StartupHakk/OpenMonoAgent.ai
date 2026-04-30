namespace OpenMono.Rendering;

public interface ITerminal
{
    int WindowWidth  { get; }
    int WindowHeight { get; }
    bool IsOutputRedirected { get; }

    event Action<ConsoleSpecialKey>? InterruptRequested;

    ValueTask WriteAsync(string value, CancellationToken ct = default);
    ValueTask WriteLineAsync(string value, CancellationToken ct = default);

    ConsoleKeyInfo? TryReadKey();

    ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default);
}
