// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Omabot.Common;
using Omabot.Team;

namespace Omabot.Runtime;

/// <summary>
/// The per-turn gate file (botcfg/turn-gate, mounted read only at
/// /omabot/bot/turn-gate). The roster writes it before each turn it starts;
/// the bash pre tool hook blocks every tool listed as blocked. OMA checks its
/// own permissions before hooks run, so this is how a routine or consult turn
/// on an edits or auto bot is kept from writing without a person (plan 7.4
/// "permission profile forced to ask", 7.5 consult).
/// </summary>
public static class TurnGate
{
    public const string FileName = "turn-gate";

    /// <summary>Tools that change things. Read-only tools, roster tools and pausing tools (HostExec, AskUser) are not listed.</summary>
    public static readonly string[] ChangingTools = ["FileWrite", "FileEdit", "ApplyPatch", "Bash", "Playbook", "Agent", "ImplementPlan", "MemorySave"];

    public static string PathFor(RosterPaths paths, string slug) => Path.Combine(paths.Bot(slug).Root, "botcfg", FileName);

    public static void Write(RosterPaths paths, string slug, string origin, IEnumerable<string> blocked)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(origin, "^[a-z_]{1,24}$")) throw new ArgumentException("bad origin label");
        var sb = new StringBuilder();
        sb.Append("origin=").Append(origin).Append('\n');
        foreach (var t in blocked.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            if (!Names.IsToolName(t)) continue;
            sb.Append("blocked=").Append(t).Append('\n');
        }
        var dir = Path.GetDirectoryName(PathFor(paths, slug))!;
        SecureFile.EnsureDirNoClobber(dir);
        SecureFile.WriteAllTextAtomic(PathFor(paths, slug), sb.ToString(), SecureFile.PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public static void Reset(RosterPaths paths, string slug) => Write(paths, slug, "none", []);

    /// <summary>
    /// Routine runs on an edits or auto bot may not use changing tools without a
    /// person. On an ask bot OMA already pauses for them, and the broker never
    /// auto-allows in a routine turn.
    /// </summary>
    public static IReadOnlyList<string> RoutineBlocks(BotSpec spec)
        => spec.PermissionProfile == "ask" ? [] : [.. ChangingTools.Where(spec.ToolsAllow.Contains)];

    /// <summary>Consult turns act on nothing, whatever the profile (plan 7.5).</summary>
    public static IReadOnlyList<string> ConsultBlocks(BotSpec spec) => [.. ChangingTools.Concat(["HostExec"]).Where(spec.ToolsAllow.Contains)];

    public static (string Origin, IReadOnlyList<string> Blocked) Read(RosterPaths paths, string slug)
    {
        var f = PathFor(paths, slug);
        if (!File.Exists(f)) return ("none", []);
        var origin = "none";
        var blocked = new List<string>();
        foreach (var line in File.ReadAllLines(f))
        {
            if (line.StartsWith("origin=", StringComparison.Ordinal)) origin = line[7..];
            else if (line.StartsWith("blocked=", StringComparison.Ordinal)) blocked.Add(line[8..]);
        }
        return (origin, blocked);
    }
}
