namespace OpenMono.Win.AgentHost;

// Upstream McpClient spawns config.Command with UseShellExecute=false, which
// cannot launch npm-style .cmd/.bat shims on Windows. This resolver runs
// before spawn: .exe targets launch directly, script shims are wrapped in
// cmd.exe /c, and anything unresolvable is returned untouched so the normal
// upstream error surfaces instead of a new one.
public sealed record ResolvedMcpCommand(string FileName, string ArgsPrefix, bool DirectLaunch);

public static class WindowsMcpLauncher
{
    public static ResolvedMcpCommand Resolve(string command, Func<string, string?> findOnPath)
    {
        var trimmed = command.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower.EndsWith(".exe", StringComparison.Ordinal))
        {
            var path = LooksLikePath(trimmed) ? trimmed : findOnPath(trimmed);
            return new ResolvedMcpCommand(Quote(path ?? trimmed), string.Empty, true);
        }

        if (lower.EndsWith(".cmd", StringComparison.Ordinal) || lower.EndsWith(".bat", StringComparison.Ordinal))
        {
            var path = LooksLikePath(trimmed) ? trimmed : findOnPath(trimmed);
            return new ResolvedMcpCommand("cmd.exe", "/c " + Quote(path ?? trimmed), false);
        }

        var direct = findOnPath(trimmed + ".exe");
        if (direct is not null)
        {
            return new ResolvedMcpCommand(Quote(direct), string.Empty, true);
        }

        var shim = findOnPath(trimmed + ".cmd") ?? findOnPath(trimmed + ".bat");
        if (shim is not null)
        {
            return new ResolvedMcpCommand("cmd.exe", "/c " + Quote(shim), false);
        }

        var exact = findOnPath(trimmed);
        if (exact is not null)
        {
            return exact.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? new ResolvedMcpCommand(Quote(exact), string.Empty, true)
                : new ResolvedMcpCommand("cmd.exe", "/c " + Quote(exact), false);
        }

        return new ResolvedMcpCommand(trimmed, string.Empty, true);
    }

    private static bool LooksLikePath(string command) =>
        command.Contains(Path.DirectorySeparatorChar)
        || command.Contains(Path.AltDirectorySeparatorChar);

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;
}
