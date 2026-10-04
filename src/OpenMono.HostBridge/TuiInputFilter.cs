using System.Text.RegularExpressions;

namespace OpenMono.HostBridge;

/// <summary>
/// Guards the TUI input loop against keystrokes that predate it: the shell
/// line that launched the bridge (e.g. <c>openmono agent --host</c>) can
/// linger in the console buffer and would otherwise be sent to the agent as
/// its first task.
/// </summary>
public static class TuiInputFilter
{
    private static readonly Regex LauncherEcho = new(
        @"^\s*(sudo\s+)?(\S+\s+)?\S*openmono(\.exe|\.sh)?\s+agent\s+--host(\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsLauncherEcho(string line) =>
        !string.IsNullOrWhiteSpace(line) && LauncherEcho.IsMatch(line);

    /// <summary>Discard keystrokes already queued before the TUI takes over.</summary>
    public static void DrainStdin()
    {
        try
        {
            if (Console.IsInputRedirected)
                return;
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true);
        }
        catch (Exception)
        {
        }
    }
}
