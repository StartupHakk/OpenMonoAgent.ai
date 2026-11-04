// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Db;
using Omabot.Render;
using Omabot.Team;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed partial class RenderTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private async Task<BotStore> BotsAsync() => new(await Applied.DefaultPlusTwoAsync(_r), _r.Clock);

    private JsonObject Settings(string slug) => JsonNode.Parse(File.ReadAllText(_r.Paths.Bot(slug).Settings))!.AsObject();

    [Fact(DisplayName = "INT-09 rendered settings use the bridge gateway, never 127.0.0.1")]
    public async Task Int09()
    {
        var bots = await BotsAsync();
        foreach (var row in bots.All())
        {
            var s = Settings(row.Slug);
            var endpoint = s["llm"]!["endpoint"]!.GetValue<string>();
            Assert.StartsWith($"http://{_r.Config.Gateway.BindAddress}:{_r.Config.Gateway.Port}", endpoint, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", endpoint, StringComparison.Ordinal);
            Assert.DoesNotContain("localhost", endpoint, StringComparison.Ordinal);
            Assert.Null(s["llm"]!["api_key"]);
        }
    }

    [Fact(DisplayName = "INT-10 hard rules are in the data dir system prompt after the first apply, not in the workspace")]
    public async Task Int10()
    {
        var bots = await BotsAsync();
        var rules = Templates.Get("hard-rules.md").Trim();
        foreach (var row in bots.All())
        {
            var bp = _r.Paths.Bot(row.Slug);
            var sys = File.ReadAllText(bp.SystemPrompt);
            Assert.Contains(rules, sys, StringComparison.Ordinal);
            var ws = new BotRenderer(_r.Paths, _r.Config).WorkspaceOf(row.Spec);
            Assert.False(Directory.Exists(Path.Combine(ws, ".openmono")) && File.Exists(Path.Combine(ws, ".openmono", "system-prompt.md")));
            // OPENMONO.md carries a copy too (plan 7.2), guarded by deny rules and the hash check.
            Assert.Contains(rules, File.ReadAllText(Path.Combine(ws, "OPENMONO.md")), StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "INT-11 hook commands are bash scripts that parse and block as intended")]
    public async Task Int11()
    {
        var bots = await BotsAsync();
        var hooksDir = Path.Combine(Applied.RepoRoot(), "bot", "hooks");
        foreach (var f in Directory.GetFiles(hooksDir, "*.sh"))
        {
            Assert.StartsWith("#!/bin/bash", File.ReadAllText(f), StringComparison.Ordinal);
            Assert.Equal(0, Run("bash", "-n", f).Code);
        }
        var hooks = Settings(bots.Get("Scout").Slug)["hooks"]!.AsObject();
        foreach (var (_, list) in hooks)
            foreach (var h in list!.AsArray())
            {
                var run = h!["run"]!.GetValue<string>();
                Assert.Matches(@"^/omabot/hooks/[a-z\-]+\.sh( |$)", run);
                Assert.DoesNotContain("tool_input", run, StringComparison.Ordinal);
                Assert.DoesNotContain("tool_output", run, StringComparison.Ordinal);
            }
        var allowed = Path.Combine(_r.Root, "allowed.txt");
        File.WriteAllText(allowed, "FileRead\nGrep\n");
        var pre = Path.Combine(hooksDir, "pre-tool.sh");
        Assert.Equal(0, Run("bash", pre, "FileRead", ("ROSTER_ALLOWED_TOOLS", allowed)).Code);
        Assert.Equal(2, Run("bash", pre, "Bash", ("ROSTER_ALLOWED_TOOLS", allowed)).Code);
        Assert.Equal(2, Run("bash", pre, "File;Read", ("ROSTER_ALLOWED_TOOLS", allowed)).Code);
        Assert.Equal(2, Run("bash", pre, "FileRead", ("ROSTER_ALLOWED_TOOLS", Path.Combine(_r.Root, "missing.txt"))).Code);
        Assert.Equal(2, Run("bash", Path.Combine(hooksDir, "block.sh"), "blocked_input").Code);
    }

    [Fact(DisplayName = "INT-12 every key in rendered settings.json exists in OMA AppConfig")]
    public async Task Int12()
    {
        var bots = await BotsAsync();
        var schema = AppConfigSchema.Load(Path.Combine(Applied.RepoRoot(), "src", "OpenMono.Core", "Config", "AppConfig.cs"));
        foreach (var row in bots.All())
        {
            var problems = new List<string>();
            schema.Check(Settings(row.Slug), "AppConfig", "", problems);
            Assert.True(problems.Count == 0, row.Slug + ": " + string.Join(", ", problems));
        }
        // And the checker itself catches a wrong key.
        var bad = new List<string>();
        schema.Check(JsonNode.Parse("""{"llm":{"endpointt":"x"},"permissions":{"tools":{"Bash":{"allow":[],"maybe":[]}}}}""")!.AsObject(), "AppConfig", "", bad);
        Assert.Equal(2, bad.Count);
    }

    [Fact(DisplayName = "HEALTH-01 health bot settings carry no write, approval or ACP tools")]
    public async Task Health01()
    {
        var bots = await BotsAsync();
        var health = bots.All().Single(b => b.Spec.Role == "health");
        var tools = Settings(health.Slug)["permissions"]!["tools"]!.AsObject();
        string[] writers = ["FileWrite", "FileEdit", "ApplyPatch", "Bash", "HostExec", "NotebookEdit"];
        foreach (var w in writers)
        {
            if (!tools.ContainsKey(w)) continue;
            Assert.Empty(tools[w]!["allow"]?.AsArray() ?? []);
            Assert.Contains("*", tools[w]!["deny"]!.AsArray().Select(x => x!.GetValue<string>()));
        }
        foreach (var t in health.Spec.ToolsAllow)
        {
            Assert.True(ToolCatalog.IsReadOnly(t), t);
            Assert.DoesNotContain("approve", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("decide", t, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("off", health.Spec.MemoryEnabled ? "on" : "off");
        var allowedTxt = File.ReadAllText(Path.Combine(_r.Paths.Bot(health.Slug).Root, "botcfg", "allowed-tools.txt"));
        Assert.DoesNotContain("FileWrite", allowedTxt, StringComparison.Ordinal);
        Assert.DoesNotContain("Bash", allowedTxt, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Protected files are denied for write tools on every bot")]
    public async Task ProtectedFiles()
    {
        var bots = await BotsAsync();
        var s = Settings(bots.Get("Builder").Slug)["permissions"]!["tools"]!.AsObject();
        var deny = s["FileWrite"]!["deny"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(deny, d => d.Contains("OPENMONO.md", StringComparison.Ordinal));
        Assert.Contains(deny, d => d.Contains(".openmono", StringComparison.Ordinal));
        var bash = s["Bash"]!.AsObject();
        Assert.Contains("dotnet test*", bash["allow"]!.AsArray().Select(x => x!.GetValue<string>()));
        var scout = Settings(bots.Get("Scout").Slug)["permissions"]!["tools"]!.AsObject();
        Assert.Contains("*", scout["FileWrite"]!["deny"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("*", scout["HostExec"]!["ask"]!.AsArray().Select(x => x!.GetValue<string>()).Concat(scout["HostExec"]!["deny"]!.AsArray().Select(x => x!.GetValue<string>())));
    }

    [Fact(DisplayName = "Blocked inputs include the ACP port, sockets and metadata hosts")]
    public async Task BlockedInputs()
    {
        var bots = await BotsAsync();
        var pre = Settings(bots.Get("Scout").Slug)["hooks"]!["pre_tool_use"]!.AsArray();
        var inputs = pre.Select(h => h!["if"]?["input_contains"]?.GetValue<string>()).Where(x => x is not null).ToList();
        foreach (var must in new[] { ":7475", "/api/v1/sessions", "docker.sock", "supervisor.sock", "169.254.169.254" }) Assert.Contains(must, inputs);
    }

    [Fact(DisplayName = "Rendering is stable: same spec gives the same OPENMONO.md hash")]
    public async Task Stable()
    {
        var bots = await BotsAsync();
        var row = bots.Get("Scout");
        var r = new BotRenderer(_r.Paths, _r.Config);
        var ctx = new RenderContext("http://172.30.0.1:7480/v1", false, null, []);
        Assert.Equal(r.Render(row.Spec, ctx).OpenMonoHash, r.Render(row.Spec, ctx).OpenMonoHash);
    }

    [Fact(DisplayName = "OPENMONO.md stays under 200 lines and 25 KB")]
    public async Task OpenMonoCap()
    {
        var bots = await BotsAsync();
        var r = new BotRenderer(_r.Paths, _r.Config);
        var row = bots.Get("head-honcho");
        var md = r.RenderOpenMonoMd(row.Spec, new RenderContext("http://x/v1", false, null, [], new string('m', 40_000)));
        Assert.True(md.Split('\n').Length <= 200);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(md) <= 25 * 1024);
        Assert.Contains(Templates.Get("hard-rules.md").Trim(), md, StringComparison.Ordinal);
        var many = string.Join("\n", Enumerable.Range(0, 400).Select(i => "- note " + i));
        var md2 = r.RenderOpenMonoMd(row.Spec, new RenderContext("http://x/v1", false, null, [], many, many));
        Assert.True(md2.Split('\n').Length <= 200);
        Assert.Contains("trimmed", md2, StringComparison.Ordinal);
    }

    private static (int Code, string Out) Run(string file, string a1, string? a2 = null, (string K, string V)? env = null)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(a1);
        if (a2 is not null) psi.ArgumentList.Add(a2);
        if (env is { } e) psi.Environment[e.K] = e.V;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    private static (int Code, string Out) Run(string file, string a1, string a2, (string K, string V) env) => Run(file, a1, a2, (env.K, env.V) as (string, string)?);
}

/// <summary>Reads class and property names out of OMA's AppConfig.cs (read only) to check rendered keys.</summary>
public sealed partial class AppConfigSchema
{
    private readonly Dictionary<string, Dictionary<string, string>> _classes = [];
    private readonly Dictionary<string, string> _bases = [];

    public static AppConfigSchema Load(string file)
    {
        var s = new AppConfigSchema();
        string? current = null;
        string? pendingName = null;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            var c = ClassRx().Match(line);
            if (c.Success)
            {
                current = c.Groups[1].Value;
                s._classes[current] = [];
                if (c.Groups[2].Success) s._bases[current] = c.Groups[2].Value;
                continue;
            }
            var jp = JsonNameRx().Match(line);
            if (jp.Success) { pendingName = jp.Groups[1].Value; continue; }
            if (line.StartsWith("[JsonIgnore", StringComparison.Ordinal)) { pendingName = "\0ignore"; continue; }
            var p = PropRx().Match(line);
            if (p.Success && current is not null)
            {
                if (pendingName != "\0ignore")
                    s._classes[current][pendingName ?? Snake(p.Groups[2].Value)] = p.Groups[1].Value.Replace("?", "", StringComparison.Ordinal).Trim();
                pendingName = null;
            }
            else if (line.Length > 0 && !line.StartsWith('[')) pendingName = null;
        }
        return s;
    }

    private Dictionary<string, string> Props(string cls)
    {
        var all = new Dictionary<string, string>(_classes[cls]);
        if (_bases.TryGetValue(cls, out var b) && _classes.ContainsKey(b))
            foreach (var kv in Props(b)) all.TryAdd(kv.Key, kv.Value);
        return all;
    }

    public void Check(JsonNode? node, string type, string path, List<string> problems)
    {
        if (node is null) return;
        if (_classes.ContainsKey(type))
        {
            if (node is not JsonObject obj) { problems.Add(path + " should be an object"); return; }
            var props = Props(type);
            foreach (var (k, v) in obj)
            {
                if (!props.TryGetValue(k, out var t)) { problems.Add($"{path}.{k} is not an OMA setting"); continue; }
                Check(v, t, path + "." + k, problems);
            }
            return;
        }
        var dict = DictRx().Match(type);
        if (dict.Success && node is JsonObject d)
        {
            foreach (var (k, v) in d) Check(v, dict.Groups[1].Value.Trim(), $"{path}[{k}]", problems);
            return;
        }
        var list = ListRx().Match(type);
        if (list.Success && node is JsonArray a)
        {
            foreach (var v in a) Check(v, list.Groups[1].Value.Trim(), path + "[]", problems);
        }
    }

    private static string Snake(string pascal) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(pascal);

    [GeneratedRegex(@"^public (?:sealed )?class (\w+)(?:\s*:\s*(\w+))?")]
    private static partial Regex ClassRx();
    [GeneratedRegex(@"^\[JsonPropertyName\(""([^""]+)""\)\]")]
    private static partial Regex JsonNameRx();
    [GeneratedRegex(@"^public (?:required )?([\w<>,\s\?\[\]]+?) (\w+) \{ get;")]
    private static partial Regex PropRx();
    [GeneratedRegex(@"^Dictionary<string,\s*(.+)>$")]
    private static partial Regex DictRx();
    [GeneratedRegex(@"^(?:List<(.+)>|(.+)\[\])$")]
    private static partial Regex ListRx();
}
