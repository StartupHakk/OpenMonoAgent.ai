using System.Text.RegularExpressions;

namespace OpenMono.Tools;

/// <summary>
/// Single source of truth for which tools may run while the session is in Plan mode.
///
/// The rule: only read-only tools are allowed in Plan mode. This is enforced server-side
/// in <c>LocalToolExecutor</c> as a HARD gate — independent of what the LLM was told in the
/// system prompt and independent of the tool-definition filtering. The prompt and filtering
/// are advisory (an LLM can hallucinate a tool call for a tool it was never offered); this
/// policy is the actual check-and-balance that cannot be bypassed.
///
/// The same predicate is used to filter the tool definitions sent to the LLM and to build
/// the plan-mode banner, so all three stay consistent by construction.
///
/// One deliberate exception: <c>HostExec</c>. Plan mode forbids *mutating* the system, not
/// *inspecting* it, and the container's own tools cannot see host paths at all — so a
/// planning agent debugging a host-side problem would be blind. HostExec is therefore
/// offered in Plan mode (when paired) but the executor gate only lets the call through
/// when the command is a read-only host inspection per <see cref="HostCommandAllowedInPlanMode"/>
/// (plus the standard per-call consent card, deny-list, and sudo policy underneath).
/// </summary>
public static partial class PlanModePolicy
{
    /// <summary>True if <paramref name="tool"/> is permitted while in Plan mode.</summary>
    public static bool IsToolAllowed(ITool tool) => tool.IsReadOnly;

    /// <summary>True when <paramref name="tool"/> is the host-execution tool.</summary>
    public static bool IsHostExec(ITool tool) =>
        string.Equals(tool.Name, "HostExec", StringComparison.OrdinalIgnoreCase);

    /// <summary>Generic, user-facing message returned when a tool is blocked in Plan mode.</summary>
    public static string BlockedMessage(string toolName) =>
        $"'{toolName}' is not available in Plan mode (read-only). " +
        $"Switch to Build mode using the Plan/Build toggle to make changes, then try again. " +
        $"Do not claim the action was performed.";

    /// <summary>Message returned when a HostExec command is refused because it is not a read-only inspection.</summary>
    public static string HostBlockedMessage() =>
        "'HostExec' is allowed in Plan mode for READ-ONLY host inspection only " +
        "(ls, cat, head, tail, grep, rg, find, stat, df, du, wc, ps, lsof, journalctl, " +
        "docker ps/logs/inspect/images, git status/log/diff/show/branch, systemctl status, " +
        "launchctl list, brew list, curl GET, --version checks). " +
        "The command was not recognized as read-only (or asked for sudo/background), so nothing ran. " +
        "Issue an inspection command instead, or switch to Build mode for anything that changes state. " +
        "Do not claim the action was performed.";

    // Fail-closed classifier for host commands issued while planning. This is a
    // guardrail, not the security boundary: every HostExec still needs a human
    // consent card, and the daemon re-enforces the deny-list + sudo + audit.
    // Substitution and redirection can run or write anything we cannot inspect,
    // so those refuse outright. Everything else must match the allowlist.
    private static readonly HashSet<string> PlainReadOnlyPrograms = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "less", "more", "grep", "rg", "fgrep", "egrep",
        "ls", "ll", "dir", "tree", "find", "stat", "file", "wc", "du", "df", "pwd",
        "echo", "printf", "which", "whereis", "type", "whoami", "id", "uname",
        "hostname", "uptime", "pgrep", "pidof", "ps", "lsof", "printenv",
        "date", "seq", "sort", "uniq", "cut", "tr", "journalctl", "locale",
    };

    private static readonly HashSet<string> DockerReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        "ps", "logs", "inspect", "images", "image", "port", "top", "stats",
        "diff", "history", "info", "version",
    };

    private static readonly HashSet<string> GitReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "blame", "branch", "ls-files", "ls-tree",
        "rev-parse", "describe", "remote", "shortlog", "whatchanged", "config",
    };

    private static readonly HashSet<string> ServiceReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        "status", "list", "show", "is-active", "is-enabled", "is-failed", "catalog",
    };

    [GeneratedRegex(@"2\s*>\s*&\s*1|&>\s*/dev/null|\d?\s*>\s*/dev/null|>\s*&\s*[12]")]
    private static partial Regex SafeRedirects();

    [GeneratedRegex(@"[;&|\n\r]+")]
    private static partial Regex Segments();

    /// <summary>
    /// True when a HostExec command issued while in Plan mode is a read-only host
    /// inspection: every pipeline segment starts with an allow-listed inspection
    /// program, no sudo, no background, no command substitution, no file writes.
    /// Anything unrecognized fails closed.
    /// </summary>
    public static bool HostCommandAllowedInPlanMode(string? command, bool asRoot, bool background)
    {
        if (asRoot || background) return false;
        if (string.IsNullOrWhiteSpace(command) || command.Length > 2000) return false;

        // Command/parameter substitution can run arbitrary code we cannot inspect.
        if (command.Contains("$(") || command.Contains("${") || command.Contains('`') ||
            command.Contains("<(") || command.Contains(">("))
            return false;

        // Strip the stream-safe redirects, then any surviving '>' is a write.
        var stripped = SafeRedirects().Replace(command, " ");
        if (stripped.Contains('>')) return false;

        var segments = Segments().Split(stripped);
        var sawSegment = false;
        foreach (var raw in segments)
        {
            var seg = raw.Trim();
            if (seg.Length == 0) continue;
            sawSegment = true;
            if (!IsSegmentReadOnly(seg)) return false;
        }
        return sawSegment;
    }

    private static bool IsSegmentReadOnly(string segment)
    {
        var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        var prog = tokens[0];
        var rest = segment[prog.Length..];

        // Wrappers that can smuggle anything: refuse rather than analyze depth.
        if (prog is "sudo" or "sh" or "bash" or "zsh" or "fish" or "xargs" or "command" or "timeout" or "nohup")
            return false;

        if (PlainReadOnlyPrograms.Contains(prog))
        {
            // find -delete/-exec mutate despite find being an inspection tool.
            if (prog == "find" && (rest.Contains("-delete") || rest.Contains("-exec") || rest.Contains("-ok")))
                return false;
            // journalctl --vacuum-* prunes the journal despite journalctl being inspection-only.
            if (prog == "journalctl" && rest.Contains("--vacuum"))
                return false;
            return true;
        }

        var sub = tokens.Length > 1 ? tokens[1] : "";
        switch (prog)
        {
            case "docker":
                return DockerReadOnlySubcommands.Contains(sub);
            case "git":
                if (sub == "config")
                    return rest.Contains("--get") || rest.Contains("--list") || rest.Contains(" -l");
                // Bare `git tag` lists; `git tag v1` writes. `git stash list` reads; push/pop writes.
                if (sub == "tag") return tokens.Length == 2;
                if (sub == "stash") return tokens.Length > 2 && tokens[2] == "list";
                return GitReadOnlySubcommands.Contains(sub);
            case "systemctl":
            case "service":
                return ServiceReadOnlySubcommands.Contains(sub) || sub == "status";
            case "launchctl":
                return sub == "list" || sub == "print";
            case "brew":
                return sub is "list" or "info" or "--prefix" or "--versions";
            case "npm" or "pnpm" or "yarn":
                return sub is "ls" or "list" or "outdated" or "--version" or "-v" or "doctor";
            case "curl":
                // GET by default; refuse the flags that write files or POST data.
                if (rest.Contains(" -o") || rest.Contains(" -O") || rest.Contains("--output") ||
                    rest.Contains(" -T") || rest.Contains("--upload") || rest.Contains(" -d ") ||
                    rest.Contains("--data") || rest.Contains(" -X ") || rest.Contains("--request"))
                    return false;
                return true;
            case "dotnet" or "node" or "python" or "python3" or "java" or "go":
                return sub is "--version" or "-v" or "-V" or "--info" or "version";
            default:
                return false;
        }
    }
}
