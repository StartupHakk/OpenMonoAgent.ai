namespace OpenMono.Win.AgentHost;

// Windows shell preference for the Bash tool and hook runner overrides.
// OMA hardcodes /bin/bash upstream; the Windows host resolves from this
// ordered list instead, without editing upstream files.
public static class WindowsShellSelector
{
    public static IReadOnlyList<string> PreferredShells() =>
        ["pwsh.exe", "powershell.exe", "cmd.exe"];

    public static string ResolveAvailable(Func<string, bool> exists)
    {
        foreach (var shell in PreferredShells())
        {
            if (exists(shell))
            {
                return shell;
            }
        }

        throw new InvalidOperationException(
            "No Windows shell found (looked for pwsh.exe, powershell.exe, cmd.exe).");
    }
}
