namespace OpenMono.Win.AgentHost;

// Windows shell for everything that runs on the Windows host: install,
// bootstrap, first-run, Foundry start/stop, hooks, and the agent shell tool.
// PowerShell only: pwsh.exe first, then powershell.exe. No Git Bash wrapping,
// no cmd.exe fallback on the host path: falling back to a different shell
// language would silently mistranslate commands, so a machine with neither
// PowerShell gets a clear error instead. (Launching a tool's own .cmd shim
// via WindowsMcpLauncher is not a shell choice and is unaffected.)
// The agent tool keeps the name "Bash" so prompts and behavior stay
// consistent with the rest of OMA; on Windows it executes PowerShell.
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
