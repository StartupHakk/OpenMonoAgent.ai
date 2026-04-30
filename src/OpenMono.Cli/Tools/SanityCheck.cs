using System.Text.Json;

namespace OpenMono.Tools;

public static class SanityCheck
{

    private static readonly string[] QuickDestructivePatterns =
    [
        ":(){:|:&};:",
        "dd if=/dev/zero of=/dev/",
        "dd if=/dev/random of=/dev/",
        "> /dev/sda",
    ];

    private static readonly string[] ProcessSubstitutionPatterns = [">(", "<(", "=("];

    private static readonly string[] ProtectedSystemPaths =
    [
        "/etc/",
        "/usr/bin/",
        "/usr/sbin/",
        "/sbin/",
        "/bin/",
        "/boot/",
        "/sys/",
        "/proc/",
        "/dev/",
        "/system/",
        "/library/",
    ];

    public static string? Check(string toolName, JsonElement input, string workingDirectory)
    {
        return toolName switch
        {
            "Bash" => CheckBash(input),
            "FileWrite" or "FileEdit" or "ApplyPatch" => CheckFileMutation(toolName, input, workingDirectory),
            _ => null,
        };
    }

    public static bool IsDestructiveCommand(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();

        if (QuickDestructivePatterns.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        var parseResult = BashParser.Parse(command);
        return BashParser.CheckDestructive(parseResult) is not null;
    }

    private static readonly HashSet<string> PermissionModifyingCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chmod", "chown", "chattr", "setfacl",
            "icacls", "takeown", "attrib",
        };

    private static readonly HashSet<string> AlwaysBlockedBuiltins =
        new(StringComparer.OrdinalIgnoreCase) { "eval", "exec" };

    private static readonly HashSet<string> InlineExecInterpreters =
        new(StringComparer.OrdinalIgnoreCase)
        { "python", "python3", "python2", "node", "nodejs", "perl", "ruby", "php", "lua" };

    private static readonly HashSet<string> InlineExecFlags =
        new(StringComparer.OrdinalIgnoreCase) { "-c", "-e", "-r" };

    private static string? CheckBash(JsonElement input)
    {
        if (!input.TryGetProperty("command", out var cmdEl) || cmdEl.GetString() is not { } command)
            return null;

        var normalized = command.Trim().ToLowerInvariant();

        foreach (var pattern in QuickDestructivePatterns)
        {
            if (normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return $"SanityCheck refused Bash: Destructive pattern detected ({pattern}). Command: {Truncate(command, 200)}";
        }

        foreach (var pattern in ProcessSubstitutionPatterns)
        {
            if (command.Contains(pattern, StringComparison.Ordinal))
                return $"SanityCheck refused Bash: process substitution '{pattern}' executes hidden subcommands " +
                       "that bypass file access checks. Use explicit pipelines instead.";
        }

        var parseResult = BashParser.Parse(command);
        var destructiveReason = BashParser.CheckDestructive(parseResult);

        if (destructiveReason is not null)
            return $"SanityCheck refused Bash: {destructiveReason}. Command: {Truncate(command, 200)}";

        foreach (var seg in parseResult.Segments)
        {

            if (PermissionModifyingCommands.Contains(seg.Binary))
                return $"SanityCheck refused Bash: '{seg.Binary}' modifies filesystem permissions and cannot run autonomously. " +
                       "Ask the user to run this command manually.";

            if (string.Equals(seg.Binary, "sed", StringComparison.OrdinalIgnoreCase))
            {
                var hasInPlace = seg.Args.Any(a =>
                    a == "-i" || a == "--in-place" ||
                    (a.StartsWith('-') && !a.StartsWith("--") && a.Contains('i')));
                if (hasInPlace)
                    return "SanityCheck refused Bash: 'sed -i' modifies files in-place — use FileEdit instead. " +
                           "FileEdit has permission checks, undo history, and secret scanning.";
            }

            var interpreterReason = CheckInterpreterAbuse(seg);
            if (interpreterReason is not null)
                return interpreterReason;
        }

        return null;
    }

    private static string? CheckInterpreterAbuse(CommandSegment seg)
    {

        if (AlwaysBlockedBuiltins.Contains(seg.Binary))
            return $"SanityCheck refused Bash: '{seg.Binary}' executes arbitrary code strings. " +
                   "Write the logic to a script file and execute that instead.";

        if (InlineExecInterpreters.Contains(seg.Binary) && seg.Args.Any(InlineExecFlags.Contains))
            return $"SanityCheck refused Bash: '{seg.Binary}' with inline code flag (-c/-e/-r) requires explicit " +
                   "user approval. Write the code to a script file and run that instead.";

        return null;
    }

    private static string? CheckFileMutation(string toolName, JsonElement input, string workingDirectory)
    {
        if (!input.TryGetProperty("file_path", out var pathEl) || pathEl.GetString() is not { } filePath)
            return null;

        string resolved;
        try
        {
            resolved = Path.GetFullPath(filePath, workingDirectory);
        }
        catch (Exception ex)
        {
            return $"SanityCheck refused {toolName}: invalid file path '{filePath}' ({ex.Message})";
        }

        var lower = resolved.Replace('\\', '/').ToLowerInvariant();

        foreach (var protectedPath in ProtectedSystemPaths)
        {
            if (lower.StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase))
                return $"SanityCheck refused {toolName}: write to protected system path '{resolved}' is not allowed.";
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            string[] credentialDirs = [".ssh", ".aws", ".gnupg", ".config/gcloud", ".kube"];
            foreach (var dir in credentialDirs)
            {
                var fullCred = Path.GetFullPath(Path.Combine(home, dir)) + Path.DirectorySeparatorChar;
                if (resolved.StartsWith(fullCred, StringComparison.OrdinalIgnoreCase))
                    return $"SanityCheck refused {toolName}: write to credential directory '{fullCred}' is not allowed.";
            }
        }

        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
