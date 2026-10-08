namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Clipboard bridge for the desktop app. The native chat UI uses the WinUI 3
/// clipboard API directly. For parity with the bash bridge, the supervisor can
/// watch the OMA clipboard bridge file and mirror it to the Windows clipboard.
/// Win32 P/Invoke stays in windows/; OMA AnsiInputReader is not edited.
/// </summary>
public sealed class ClipboardService
{
    private readonly Func<string, CancellationToken, Task> _setText;
    private readonly Func<CancellationToken, Task<string?>> _getText;

    public ClipboardService(
        Func<string, CancellationToken, Task> setText,
        Func<CancellationToken, Task<string?>> getText)
    {
        _setText = setText;
        _getText = getText;
    }

    public static string BridgeFilePath(string dataDirectory) =>
        Path.Combine(dataDirectory, ".clipboard-out");

    public Task CopyAsync(string text, CancellationToken ct = default) => _setText(text, ct);

    public Task<string?> ReadAsync(CancellationToken ct = default) => _getText(ct);

    /// <summary>
    /// One shot mirror of the OMA file bridge into the Windows clipboard.
    /// Returns the mirrored text, or null when there is nothing new.
    /// </summary>
    public async Task<string?> MirrorBridgeFileAsync(string dataDirectory, CancellationToken ct = default)
    {
        var bridge = BridgeFilePath(dataDirectory);
        if (!File.Exists(bridge))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(bridge, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        await _setText(text, ct);
        return text;
    }
}
