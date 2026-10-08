using System.Text.RegularExpressions;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Windows aware destructive command detection used as a decorator in front of
/// OMA SanityCheck. OMA SanityCheck.cs is not edited; this runs first in the
/// Windows host and in WindowsShellTool.RequiredPermission.
/// </summary>
public static class WindowsGuardrails
{
    private static readonly string[] DenySubstrings =
    [
        "format ",
        "diskpart",
        "bcdedit",
        "reg delete",
        "remove-item -recurse c:\\",
        "remove-item c:\\windows",
        "del /s /q c:\\windows",
        "rmdir /s /q c:\\windows",
        "cipher /w:",
        "vssadmin delete",
        "wbadmin delete",
        "bcdboot",
        "bootrec",
        "takeown /f c:\\windows",
        "icacls c:\\windows",
    ];

    private static readonly Regex CredentialPath = new(
        @"(%appdata%|%localappdata%|%userprofile%)[\\/].*(credentials|passwords|secrets|tokens)|ntuser\.dat|\bsam\b.*hive|\.pem$|\.pfx$|\.env$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] AskSubstrings =
    [
        "runas",
        "-verb runas",
        "reg add",
        "sc ",
        "schtasks",
        "-encodedcommand",
        "invoke-expression",
        "iex ",
        "start-process -verb",
        "set-executionpolicy",
    ];

    public static bool IsDestructive(string command)
    {
        var normalized = command.ToLowerInvariant();
        if (DenySubstrings.Any(p => normalized.Contains(p, StringComparison.Ordinal)))
        {
            return true;
        }

        return CredentialPath.IsMatch(command) && WritesToCredentialPath(command);
    }

    public static bool NeedsAsk(string command)
    {
        var normalized = command.ToLowerInvariant();
        return AskSubstrings.Any(p => normalized.Contains(p, StringComparison.Ordinal));
    }

    private static bool WritesToCredentialPath(string command)
    {
        var normalized = command.ToLowerInvariant();
        return normalized.Contains("out-file", StringComparison.Ordinal)
            || normalized.Contains("set-content", StringComparison.Ordinal)
            || normalized.Contains('>', StringComparison.Ordinal)
            || normalized.Contains("copy", StringComparison.Ordinal)
            || normalized.Contains("move", StringComparison.Ordinal);
    }
}
