// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Common;

namespace Omabot.Release;

public sealed record GateLine(string Check, bool Ok, string Message);

/// <summary>A known advisory: a package, the versions it affects, and how bad it is.</summary>
public sealed record Advisory(string Package, IReadOnlyList<string> Versions, string Severity, string Id);

/// <summary>
/// The security release gate's automatic part (plan 11.8, SEC-25): no dash
/// characters and no secrets in the roster files, every package pinned to one
/// version, no known critical advisory against a pinned package, image pins
/// required by the recipes, and Spencer's sign-off recorded. The tests named
/// in 11.8 run in CI beside this; a person signs off last.
/// </summary>
public static partial class ReleaseGate
{
    [GeneratedRegex("<PackageReference\\s+Include=\"([^\"]+)\"\\s+Version=\"([^\"]*)\"")]
    private static partial Regex PackageRef();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(-[0-9A-Za-z.]+)?$")]
    private static partial Regex ExactVersion();

    private static IEnumerable<string> Files(string bot, string pattern = "*")
        => Directory.EnumerateFiles(bot, pattern, SearchOption.AllDirectories).Where(f =>
        {
            var rel = Path.GetRelativePath(bot, f).Replace('\\', '/');
            return !(rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)
                || rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal));
        });

    /// <summary>Every package reference in the roster projects with its pinned version.</summary>
    public static IReadOnlyList<(string Project, string Package, string Version)> Packages(string bot)
        => [.. Files(bot, "*.csproj").SelectMany(f => PackageRef().Matches(File.ReadAllText(f)).Select(m => (Path.GetRelativePath(bot, f), m.Groups[1].Value, m.Groups[2].Value)))];

    public static IReadOnlyList<Advisory> ReadAdvisories(string file)
    {
        var list = new List<Advisory>();
        foreach (var n in JsonNode.Parse(File.ReadAllText(file))?.AsArray() ?? [])
        {
            if (n is not JsonObject o) continue;
            var versions = o["versions"] is JsonArray a ? a.Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList() : [];
            list.Add(new Advisory(o["package"]?.GetValue<string>() ?? "", versions, (o["severity"]?.GetValue<string>() ?? "").ToLowerInvariant(), o["id"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    /// <summary>Advisories from `dotnet list package --vulnerable --include-transitive --format json`; null with a reason when it cannot be read.</summary>
    public static (IReadOnlyList<Advisory>? List, string? Problem) OnlineAdvisories(string bot)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = bot };
            foreach (var arg in new[] { "list", "Omabot.sln", "package", "--vulnerable", "--include-transitive", "--format", "json" }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return (null, "dotnet did not start");
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return (null, "dotnet list package --vulnerable failed");
            var list = new List<Advisory>();
            foreach (var project in JsonNode.Parse(output)?["projects"]?.AsArray() ?? [])
                foreach (var fw in project?["frameworks"]?.AsArray() ?? [])
                    foreach (var kind in new[] { "topLevelPackages", "transitivePackages" })
                        foreach (var pkg in fw?[kind]?.AsArray() ?? [])
                            foreach (var v in pkg?["vulnerabilities"]?.AsArray() ?? [])
                                list.Add(new Advisory(pkg!["id"]?.GetValue<string>() ?? "", [pkg["resolvedVersion"]?.GetValue<string>() ?? ""],
                                    (v?["severity"]?.GetValue<string>() ?? "").ToLowerInvariant(), v?["advisoryurl"]?.GetValue<string>() ?? ""));
            return (list, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }

    public static IReadOnlyList<GateLine> Check(string bot, IReadOnlyList<Advisory>? advisories, string? advisoryProblem, bool multi)
    {
        var lines = new List<GateLine>();
        var dashed = Files(bot).Where(f => new FileInfo(f).Length < 5_000_000).Where(f => { var t = File.ReadAllText(f); return t.Contains('\u2014') || t.Contains('\u2013'); })
            .Select(f => Path.GetRelativePath(bot, f)).ToList();
        lines.Add(new("dash", dashed.Count == 0, dashed.Count == 0 ? "No em or en dash in any roster file." : "Dashes in: " + string.Join(", ", dashed)));

        var scanned = Directory.EnumerateFiles(Path.Combine(bot, "docs"), "*", SearchOption.AllDirectories)
            .Concat(Directory.Exists(Path.Combine(bot, "templates")) ? Directory.EnumerateFiles(Path.Combine(bot, "templates"), "*", SearchOption.AllDirectories) : [])
            .Append(Path.Combine(bot, "README.md")).Where(File.Exists);
        var leaks = new List<string>();
        foreach (var f in scanned)
        {
            var text = File.ReadAllLines(f);
            for (var i = 0; i < text.Length; i++) if (Redactor.ContainsStrongSecret(text[i])) leaks.Add($"{Path.GetRelativePath(bot, f)}:{i + 1}");
        }
        lines.Add(new("secrets", leaks.Count == 0, leaks.Count == 0 ? "No key, token or password in docs, templates or the README." : "Possible secrets: " + string.Join(", ", leaks)));

        var packages = Packages(bot);
        var loose = packages.Where(p => !ExactVersion().IsMatch(p.Version)).Select(p => $"{p.Package} {p.Version} ({p.Project})").ToList();
        lines.Add(new("pins", loose.Count == 0 && packages.Count > 0, loose.Count == 0 ? $"{packages.Count} package references, each pinned to one version." : "Not pinned: " + string.Join(", ", loose)));

        if (advisories is null)
            lines.Add(new("advisories", false, "Release blocked: no advisory data (" + (advisoryProblem ?? "run with --online or --advisories FILE") + ")."));
        else
        {
            var hits = advisories.Where(a => packages.Any(p => string.Equals(p.Package, a.Package, StringComparison.OrdinalIgnoreCase) && a.Versions.Contains(p.Version))).ToList();
            var critical = hits.Where(a => a.Severity == "critical").Select(a => $"{a.Package} {string.Join("/", a.Versions)} ({a.Id})").ToList();
            var other = hits.Where(a => a.Severity != "critical").Select(a => $"{a.Package} {a.Severity} ({a.Id})").ToList();
            lines.Add(new("advisories", critical.Count == 0, critical.Count > 0 ? "Release blocked by critical advisories: " + string.Join(", ", critical)
                : other.Count > 0 ? "No critical advisory; also known: " + string.Join(", ", other) : $"No known advisory against the pinned packages ({advisories.Count} checked)."));
        }

        var desktop = Path.Combine(bot, "docker", "desktop", "Dockerfile");
        var front = Path.Combine(bot, "docker", "web-front", "compose.yaml");
        var unpinned = new List<string>();
        if (File.Exists(desktop) && !Regex.IsMatch(File.ReadAllText(desktop), "FROM [^\\s]+@\\$\\{BASE_DIGEST\\}")) unpinned.Add("docker/desktop/Dockerfile");
        if (File.Exists(front) && !File.ReadAllText(front).Contains("@${CADDY_DIGEST:?", StringComparison.Ordinal)) unpinned.Add("docker/web-front/compose.yaml");
        lines.Add(new("images", unpinned.Count == 0, unpinned.Count == 0 ? "Image recipes require a digest (build the egress gate with SDK_IMAGE and RUNTIME_IMAGE digests)." : "Recipes without a required digest: " + string.Join(", ", unpinned)));

        lines.Add(Signoff(Path.Combine(bot, "docs", "signoff.md"), multi));
        return lines;
    }

    /// <summary>Spencer's sign-off: version, commit, the gate result, his name and the date; web-multi also needs the outside review.</summary>
    public static GateLine Signoff(string file, bool multi)
    {
        if (!File.Exists(file)) return new("signoff", false, "docs/signoff.md is missing.");
        var fields = File.ReadAllLines(file).Select(l => l.Split(':', 2)).Where(x => x.Length == 2)
            .GroupBy(x => x[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.Ordinal);
        string F(string k) => fields.TryGetValue(k, out var v) ? v : "";
        var missing = new List<string>();
        if (F("version").Length == 0) missing.Add("version");
        if (!Regex.IsMatch(F("commit"), "^[0-9a-f]{7,40}$")) missing.Add("commit");
        if (F("security_gate") != "passed") missing.Add("security_gate: passed");
        if (multi && F("outside_review") != "done") missing.Add("outside_review: done");
        if (F("signed_by").Length == 0) missing.Add("signed_by");
        if (!DateOnly.TryParseExact(F("date"), "yyyy-MM-dd", out _)) missing.Add("date");
        return missing.Count == 0 ? new("signoff", true, $"Signed off by {F("signed_by")} on {F("date")} for {F("version")} at {F("commit")}.")
            : new("signoff", false, "Not signed off yet; docs/signoff.md needs " + string.Join(", ", missing) + ".");
    }
}
