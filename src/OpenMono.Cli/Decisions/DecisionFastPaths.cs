using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Tools;
using OpenMono.Utils;

namespace OpenMono.Decisions;

public static class DecisionFastPaths
{
    private static readonly HashSet<string> GatedTools =
        new(StringComparer.OrdinalIgnoreCase) { "Bash", "FileWrite", "FileEdit", "ApplyPatch" };

    private static readonly HashSet<string> PrivilegeBinaries =
        new(StringComparer.OrdinalIgnoreCase) { "sudo", "su", "doas", "pkexec", "chmod", "chown" };

    private static readonly string[] EgressMarkers =
    [
        "curl", "wget", "scp", "sftp", "rsync", "ssh ", "nc ", "telnet",
        "--data", "--post-data", "-d ", "Invoke-WebRequest", "Invoke-RestMethod",
    ];

    public static bool ShouldConsult(string toolName) => GatedTools.Contains(toolName);

    public static bool TryAllow(string toolName, JsonElement input, string workingDirectory, out string reason)
    {
        reason = string.Empty;
        if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!input.TryGetProperty("command", out var cmdEl) || cmdEl.GetString() is not { } command)
            return false;
        if (string.IsNullOrWhiteSpace(command))
            return false;
        if (!PermissionEngine.IsSafeReadOnlyCommand(ProcessExecCap.FromCommand(command.Trim())))
            return false;
        reason = "safe-read-only";
        return true;
    }

    public static bool IsDestructive(string toolName, JsonElement input)
    {
        if (!ShouldConsult(toolName))
            return false;
        return toolName.ToLowerInvariant() switch
        {
            "bash" => IsDestructiveBash(input),
            "filewrite" or "fileedit" => IsOutOfScopeWrite(input),
            "applypatch" => IsLargePatch(input),
            _ => true,
        };
    }

    private static bool IsDestructiveBash(JsonElement input)
    {
        if (!input.TryGetProperty("command", out var cmdEl) || cmdEl.GetString() is not { } command)
            return true;
        if (string.IsNullOrWhiteSpace(command))
            return true;
        if (SanityCheck.IsDestructiveCommand(command))
            return true;
        var cap = ProcessExecCap.FromCommand(command.Trim());
        if (PrivilegeBinaries.Contains(cap.Binary))
            return true;
        if (IsForcefulVcsCommand(cap))
            return true;
        if (SecretScanner.Scan(command).Count > 0)
            return true;
        var lowered = command.ToLowerInvariant();
        return EgressMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal));
    }

    private static bool IsForcefulVcsCommand(ProcessExecCap cap)
    {
        if (!cap.Binary.Equals("git", StringComparison.OrdinalIgnoreCase) || cap.Args.Count == 0)
            return false;
        var sub = cap.Args[0].ToLowerInvariant();
        var flags = new HashSet<string>(cap.Args.Skip(1), StringComparer.OrdinalIgnoreCase);
        return sub switch
        {
            "reset" when flags.Contains("--hard") => true,
            "clean" when flags.Contains("-fd") || flags.Contains("-fdx") || flags.Contains("-f") => true,
            "push" when flags.Contains("--force") || flags.Contains("-f") => true,
            "branch" when flags.Contains("-D") => true,
            "checkout" when flags.Contains(".") => true,
            _ => false,
        };
    }

    private static bool IsOutOfScopeWrite(JsonElement input)
    {
        if (!input.TryGetProperty("file_path", out var pathEl) || pathEl.GetString() is not { } filePath)
            return true;
        if (string.IsNullOrWhiteSpace(filePath))
            return true;
        return IsOutsideWorkingDirectory(filePath);
    }

    private static bool IsLargePatch(JsonElement input)
    {
        if (!input.TryGetProperty("patch", out var patchEl) || patchEl.GetString() is not { } patch)
            return true;
        if (patch.Length > 20000)
            return true;
        var fileCount = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) ||
                line.StartsWith("+++ ", StringComparison.Ordinal))
                fileCount++;
        }
        return fileCount > 5;
    }

    internal static bool IsOutsideWorkingDirectory(string filePath, string? workingDirectory = null)
    {
        var baseDir = workingDirectory ?? Directory.GetCurrentDirectory();
        string resolved;
        try
        {
            resolved = Path.GetFullPath(filePath, baseDir);
        }
        catch (Exception)
        {
            return true;
        }
        return PathGuard.Validate(resolved, baseDir) is not null;
    }
}
