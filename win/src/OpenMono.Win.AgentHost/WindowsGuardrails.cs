namespace OpenMono.Win.AgentHost;

// M2-start deny list for the Windows host. Upstream guardrails assume Unix
// paths; this screens the Windows-destructive commands before they run.
// Heuristic and conservative: unknown commands pass through to the normal
// permission flow, only known-bad shapes are denied here.
public static class WindowsGuardrails
{
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
        // Match C:\ or C:/ possibly quoted, but not C:\Users\... subpaths.
        var idx = lower.IndexOf("c:\\", StringComparison.Ordinal);
        var slash = lower.IndexOf("c:/", StringComparison.Ordinal);
        var at = idx >= 0 ? idx : slash;
        if (at < 0)
        {
            return false;
        }

        var after = lower[(at + 3)..].TrimStart('"', '\'', ' ', ';');
        if (after.Length == 0)
        {
            return true;
        }

        if (after.StartsWith("windows", StringComparison.Ordinal))
        {
            var tail = after["windows".Length..];
            return tail.Length == 0 || tail[0] is '\\' or '/' or '"' or '\'' or ' ';
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
