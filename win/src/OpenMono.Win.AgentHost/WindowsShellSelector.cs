namespace OpenMono.Win.AgentHost;

// Host-only shell for everything that runs on the Windows host: install,
// bootstrap, first-run, Foundry start/stop, Docker stack management, and host
// hooks. PowerShell only: pwsh.exe first, then powershell.exe. No Git Bash
// wrapping and no cmd.exe fallback: falling back to a different shell
// language would silently mistranslate commands, so a machine with neither
// PowerShell gets a clear error instead.
// The agent itself is untouched: it runs in the Linux container and keeps
// bash there. oma-bot stays Linux too.
public static class WindowsShellSelector
{
    public static IReadOnlyList<string> PreferredShells() =>
        ["pwsh.exe", "powershell.exe"];

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
            "No PowerShell found (looked for pwsh.exe, powershell.exe). " +
            "Install PowerShell 7 (winget install Microsoft.PowerShell) or enable Windows PowerShell.");
    }
}
