using System.Diagnostics;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Selects the shell for hook scripts and shell tools on Windows.
/// Prefers PowerShell 7 (pwsh.exe), then Windows PowerShell, then cmd.exe.
/// Mirrors the HookRunner template contract (exit code 2 means block,
/// 30 second timeout) while launching through the Windows shell.
/// </summary>
public static class HookShellSelector
{
    public enum ShellKind
    {
        PowerShell7,
        WindowsPowerShell,
        Cmd,
    }

    public static ShellKind SelectShell()
    {
        if (CommandExists("pwsh.exe") || CommandExists("pwsh"))
        {
            return ShellKind.PowerShell7;
        }

        if (OperatingSystem.IsWindows())
        {
            return ShellKind.WindowsPowerShell;
        }

        return ShellKind.Cmd;
    }

    public static string ShellExecutable(ShellKind shell) => shell switch
    {
        ShellKind.PowerShell7 => "pwsh.exe",
        ShellKind.WindowsPowerShell => "powershell.exe",
        _ => "cmd.exe",
    };

    public static string WrapCommand(ShellKind shell, string command) => shell switch
    {
        ShellKind.Cmd => $"/d /c \"{command}\"",
        _ => $"-NoProfile -NonInteractive -Command \"{command}\"",
    };

    private static bool CommandExists(string file)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
                Arguments = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit(5_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
