namespace OpenMono.Windows.AgentHost;

/// <summary>
/// OS aware system prompt overlay. Appended after the OMA system prompt so the
/// model prefers PowerShell syntax and native Windows commands. OMA
/// SystemPrompt.cs is not edited.
/// </summary>
public static class WindowsSystemPromptOverlay
{
    public static string Build(string? shellName = null)
    {
        shellName ??= HookShellSelector.SelectShell() switch
        {
            HookShellSelector.ShellKind.PowerShell7 => "PowerShell 7 (pwsh.exe)",
            HookShellSelector.ShellKind.WindowsPowerShell => "Windows PowerShell (powershell.exe)",
            _ => "cmd.exe",
        };

        return string.Join("\n",
            "Windows environment notes (OpenMono for Windows overlay):",
            $"1. The Bash tool runs on Windows using {shellName}. Prefer PowerShell syntax.",
            "2. Use native commands directly: git, dotnet, npm, rg. Avoid bash-only idioms (heredocs, tail -f, kill, chmod).",
            "3. Read background logs with Get-Content -Tail 50 <log> and stop processes with Stop-Process -Id <pid>.",
            "4. Paths use drive letters and backslashes (C:\\code\\repo). Quote paths with spaces.",
            "5. Never touch credential stores, SAM, NTUSER.DAT, browser profiles, *.env, or *.pem files.",
            "6. Package managers need explicit user approval (winget, choco) and driver installs are out of scope.");
    }
}
