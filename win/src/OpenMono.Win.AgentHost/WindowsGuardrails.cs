namespace OpenMono.Win.AgentHost;

// M2-start deny patterns for the Windows host. Upstream permissions stay the
// single pipeline (PermissionEngine: session deny-all, then config deny
// patterns, then allow, then prompt). These patterns are the Windows additions
// to merge into the Bash tool's config deny rules, not a parallel system:
// DenyPatterns returns them per tool so the host can append them to
// permissions.tools deny lists. IsDenied is the same check the host (and
// tests) can run directly; unknown commands pass through to the normal flow.
public static class WindowsGuardrails
{
    // Deny patterns for merging into AppConfig Permissions deny rules.
    // Only the Bash tool needs Windows additions today; other tools reuse
    // the upstream rules unchanged.
    public static IReadOnlyList<string> DenyPatterns(string toolName) =>
        string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase)
            ? [
                "format *",
                "diskpart*",
                "bcdedit*",
                "reg delete *",
                "Remove-Item * -Recurse *C:\\*",
                "rmdir /s *C:\\*",
            ]
            : [];
    public static bool IsDenied(string commandLine, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var first = FirstToken(commandLine);
        var program = first.Trim('"').Trim('\'');
        if (program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            program = program[..^4];
        }

        var name = Path.GetFileNameWithoutExtension(program).ToLowerInvariant();
        var rest = commandLine[first.Length..].Trim();

        if (name is "format" or "diskpart" or "bcdedit")
        {
            reason = $"'{name}' is never allowed from the agent.";
            return true;
        }

        if (name is "reg" && rest.StartsWith("delete", StringComparison.OrdinalIgnoreCase))
        {
            reason = "'reg delete' is never allowed from the agent.";
            return true;
        }

        if (IsRecursiveDeleteOfProtectedRoot(commandLine, out var protectedReason))
        {
            reason = protectedReason;
            return true;
        }

        return false;
    }

    private static bool IsRecursiveDeleteOfProtectedRoot(string commandLine, out string reason)
    {
        reason = string.Empty;
        var lower = commandLine.ToLowerInvariant();

        var recursive = lower.Contains("-recurse")
            || lower.Contains("/s")
            || lower.Contains("-r ") && (lower.Contains("remove-item") || lower.Contains("rm "));
        if (!recursive)
        {
            return false;
        }

        var destructive = lower.Contains("remove-item")
            || lower.Contains("rmdir")
            || lower.Contains("rd ")
            || lower.Contains("del ")
            || lower.Contains("erase");
        if (!destructive)
        {
            return false;
        }

        if (HasProtectedRoot(lower))
        {
            reason = "Recursive delete of a protected root (C:\\ or C:\\Windows) is never allowed.";
            return true;
        }

        return false;
    }

    private static bool HasProtectedRoot(string lower)
    {
        // Match C:\ or C:/ followed by end-of-command, a separator, or the
        // Windows directory. The path may sit anywhere in the command line,
        // with more flags after it, so inspect only the next path segment.
        foreach (var marker in new[] { "c:\\", "c:/" })
        {
            var at = 0;
            while ((at = lower.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                var raw = lower[(at + marker.Length)..];
                if (raw.Length == 0 || raw[0] is ' ' or '\t' or '"' or '\'' or ';' or '&' or '|' or '\n')
                {
                    // The drive root itself: C:\ followed by a delimiter or end.
                    return true;
                }

                var after = raw.TrimStart('"', '\'', ' ');
                var end = after.IndexOfAny(['\\', '/', ' ', '"', '\'', ';', '&', '|']);
                var segment = end < 0 ? after : after[..end];
                if (segment.Equals("windows", StringComparison.Ordinal))
                {
                    return true;
                }

                at += marker.Length;
            }
        }

        return false;
    }

    private static string FirstToken(string commandLine)
    {
        var line = commandLine.TrimStart();
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end > 0 ? line[..(end + 1)] : line;
        }

        var space = line.IndexOfAny([' ', '\t']);
        return space > 0 ? line[..space] : line;
    }
}
