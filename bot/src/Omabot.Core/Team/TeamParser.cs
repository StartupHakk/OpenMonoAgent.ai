// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Common;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Omabot.Team;

/// <summary>
/// Strict team file reader (plan 7.13). Unknown fields are refused with their
/// path. Literal secrets are refused with the field and line, never the value.
/// Text files are read relative to the team file, with LF line ends.
/// </summary>
public sealed class TeamParser
{
    public const int MaxNameLength = 60;
    public const int MaxTitleLength = 200;
    public const int MaxPersonalityBytes = 24000;
    public const int MaxRoomBriefChars = 12000;
    public const int MaxInstructionLines = 200;
    public const int MaxInstructionBytes = 25 * 1024;
    public const long MaxTeamFileBytes = 1024 * 1024;

    private readonly List<TeamError> _errors = [];
    private readonly Redactor _redactor;
    private string _baseDir = ".";

    public TeamParser(Redactor? redactor = null) => _redactor = redactor ?? Redactor.Shared;

    public static TeamFile ParseFile(string path, Redactor? redactor = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new TeamFileException([new TeamError(path, 0, "File not found.")]);
        if (new FileInfo(full).Length > MaxTeamFileBytes)
            throw new TeamFileException([new TeamError(path, 0, "Team file is larger than 1 MB.")]);
        var text = SecureFile.NormalizeLf(File.ReadAllText(full));
        return new TeamParser(redactor).Parse(text, Path.GetDirectoryName(full) ?? ".");
    }

    public TeamFile Parse(string yaml, string baseDir)
    {
        _baseDir = Path.GetFullPath(baseDir);
        var team = new TeamFile { BaseDir = _baseDir };
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new TeamFileException([new TeamError("(file)", (int)ex.Start.Line, "Not valid YAML: " + FirstLine(ex.Message))]);
        }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new TeamFileException([new TeamError("(file)", 1, "The team file must be one YAML mapping.")]);

        ScanSecrets(root, "");
        if (_errors.Count > 0) throw new TeamFileException(_errors);

        var map = Map(root, "", "version", "defaults", "models", "teams", "bots", "routines", "routes", "rooms", "blocked_hosts", "approvers");
        if (!map.TryGetValue("version", out var versionNode))
            Error("version", 1, "version: 1 is required.");
        else if (Int(versionNode, "version") is not 1)
            Error("version", Line(versionNode), "Only version 1 is supported.");
        else team.Version = 1;

        if (map.TryGetValue("defaults", out var d)) team.Defaults = ParseDefaults(d, "defaults");
        if (map.TryGetValue("models", out var m)) team.Models = ParseModels(m, "models");
        if (map.TryGetValue("teams", out var t)) team.Teams = Seq(t, "teams").Select((n, i) => ParseTeam(n, $"teams[{i}]")).ToList();
        if (map.TryGetValue("bots", out var b)) team.Bots = Seq(b, "bots").Select((n, i) => ParseBot(n, $"bots[{i}]")).ToList();
        if (map.TryGetValue("routines", out var r))
            team.Routines = Seq(r, "routines").Select((n, i) => ParseRoutine(n, $"routines[{i}]", topLevel: true)).ToList();
        if (map.TryGetValue("routes", out var ro)) team.Routes = Seq(ro, "routes").Select((n, i) => ParseRoute(n, $"routes[{i}]")).ToList();
        if (map.TryGetValue("rooms", out var rm)) team.Rooms = Seq(rm, "rooms").Select((n, i) => ParseRoom(n, $"rooms[{i}]")).ToList();
        if (map.TryGetValue("blocked_hosts", out var bh)) team.BlockedHosts = StrList(bh, "blocked_hosts");
        if (map.TryGetValue("approvers", out var ap))
            team.Approvers = ap is YamlScalarNode { Value: null or "" or "[]" } ? [] : Seq(ap, "approvers").Select((n, i) => ParseApprover(n, $"approvers[{i}]")).ToList();

        if (_errors.Count > 0) throw new TeamFileException(_errors);
        return team;
    }

    private ApproverDef ParseApprover(YamlNode node, string path)
    {
        var map = Map(node, path, "bot", "kinds");
        var a = new ApproverDef { Line = Line(node) };
        if (map.TryGetValue("bot", out var b)) a.Bot = Str(b, Join(path, "bot")) ?? "";
        if (map.TryGetValue("kinds", out var k)) a.Kinds = StrList(k, Join(path, "kinds"));
        if (a.Bot.Length == 0) Error(path, a.Line, "bot is required.");
        if (a.Kinds.Count == 0) Error(path, a.Line, "kinds is required (for example [permission]).");
        return a;
    }

    // ---- secrets -------------------------------------------------------

    private void ScanSecrets(YamlNode node, string path)
    {
        switch (node)
        {
            case YamlScalarNode s:
                if (s.Value is { } v && _redactor.ContainsSecret(v))
                    Error(path.Length == 0 ? "(file)" : path, Line(s),
                        $"Looks like a literal secret ({Redactor.Describe(v)}). Use {{ file: PATH }} or {{ env: NAME }}. The value is not shown.");
                break;
            case YamlMappingNode mapNode:
                foreach (var (k, v2) in mapNode.Children)
                {
                    var key = (k as YamlScalarNode)?.Value ?? "?";
                    if (_redactor.ContainsSecret(key))
                        Error(path.Length == 0 ? "(file)" : path, Line(k), "A field name looks like a secret. The value is not shown.");
                    ScanSecrets(v2, Join(path, key));
                }
                break;
            case YamlSequenceNode seq:
                for (var i = 0; i < seq.Children.Count; i++) ScanSecrets(seq.Children[i], $"{path}[{i}]");
                break;
        }
    }

    // ---- sections ------------------------------------------------------

    private DefaultsDef ParseDefaults(YamlNode node, string path)
    {
        var map = Map(node, path, "model", "permission_profile", "timezone", "idle_sleep_minutes", "memory", "limits", "host_exec", "desktop");
        var d = new DefaultsDef();
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "model": d.Model = Str(v, p); break;
                case "permission_profile": d.PermissionProfile = Str(v, p); break;
                case "timezone": d.Timezone = Str(v, p); break;
                case "idle_sleep_minutes": d.IdleSleepMinutes = Int(v, p); break;
                case "memory": d.Memory = ParseMemory(v, p); break;
                case "limits": d.Limits = ParseLimits(v, p); break;
                case "host_exec": d.HostExec = Bool(v, p); break;
                case "desktop": d.Desktop = Str(v, p); break;
            }
        }
        return d;
    }

    private List<ModelDef> ParseModels(YamlNode node, string path)
    {
        var list = new List<ModelDef>();
        if (node is not YamlMappingNode mapNode) { Error(path, Line(node), "Expected a mapping of nicknames."); return list; }
        foreach (var (k, v) in mapNode.Children)
        {
            var nick = (k as YamlScalarNode)?.Value ?? "";
            var p = Join(path, nick);
            var map = Map(v, p, "upstream_model", "context_size", "backend", "labels");
            var md = new ModelDef { Nickname = nick };
            foreach (var (key, val) in map)
            {
                switch (key)
                {
                    case "upstream_model": md.UpstreamModel = Str(val, Join(p, key)) ?? ""; break;
                    case "context_size": md.ContextSize = Int(val, Join(p, key)); break;
                    case "backend": md.Backend = Str(val, Join(p, key)); break;
                    case "labels": md.Labels = StrList(val, Join(p, key)); break;
                }
            }
            if (string.IsNullOrWhiteSpace(md.UpstreamModel)) Error(p, Line(v), "upstream_model is required.");
            list.Add(md);
        }
        return list;
    }

    private TeamDef ParseTeam(YamlNode node, string path)
    {
        var map = Map(node, path, "name", "lead", "brief", "prune");
        var t = new TeamDef { Line = Line(node) };
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "name": t.Name = Str(v, p) ?? ""; break;
                case "lead": t.Lead = Str(v, p); break;
                case "brief": t.Brief = Text(v, p, MaxRoomBriefChars, chars: true); break;
                case "prune":
                    if (Bool(v, p) == true) Error(p, Line(v), "prune was dropped. Apply never deletes; use oma-bot bot remove NAME.");
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(t.Name)) Error(path, t.Line, "name is required.");
        return t;
    }

    private static readonly string[] BotFields =
    [
        "name", "was", "team", "role", "title", "description", "seen_as", "personality", "plan_prompt", "instructions",
        "model", "permission_profile", "tools", "bash", "workspace", "shared_workspace", "host_exec", "desktop", "web",
        "memory", "timezone", "idle_sleep_minutes", "limits", "routines", "rooms", "accepts_from", "manages", "keywords", "secrets", "auto_allow", "preset",
    ];

    private BotDef ParseBot(YamlNode node, string path)
    {
        var map = Map(node, path, BotFields);
        var b = new BotDef { Line = Line(node) };
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "name": b.Name = Str(v, p) ?? ""; break;
                case "was": b.Was = Str(v, p); break;
                case "team": b.Team = Str(v, p); break;
                case "role": b.Role = Str(v, p); break;
                case "title": b.Title = Str(v, p); break;
                case "description": b.Description = Str(v, p); break;
                case "seen_as": b.SeenAs = Str(v, p); break;
                case "personality": b.Personality = Text(v, p, MaxPersonalityBytes, chars: false); break;
                case "plan_prompt": b.PlanPrompt = Text(v, p, MaxPersonalityBytes, chars: false); break;
                case "instructions":
                    b.Instructions = Text(v, p, MaxInstructionBytes, chars: false);
                    if (b.Instructions is { } ins && ins.Text.Split('\n').Length > MaxInstructionLines)
                        Error(p, Line(v), $"Instructions are over {MaxInstructionLines} lines (OPENMONO.md cap).");
                    break;
                case "model": b.Model = Str(v, p); break;
                case "permission_profile": b.PermissionProfile = Str(v, p); break;
                case "tools":
                    {
                        var tm = Map(v, p, "allow", "deny");
                        if (tm.TryGetValue("allow", out var a)) b.ToolsAllow = StrList(a, Join(p, "allow"));
                        if (tm.TryGetValue("deny", out var dn)) b.ToolsDeny = StrList(dn, Join(p, "deny"));
                        break;
                    }
                case "bash":
                    {
                        var bm = Map(v, p, "allow", "deny");
                        if (bm.TryGetValue("allow", out var a)) b.BashAllow = StrList(a, Join(p, "allow"));
                        if (bm.TryGetValue("deny", out var dn)) b.BashDeny = StrList(dn, Join(p, "deny"));
                        break;
                    }
                case "workspace": b.Workspace = Str(v, p); b.WorkspaceLine = Line(v); break;
                case "shared_workspace": b.SharedWorkspace = Bool(v, p); break;
                case "host_exec": b.HostExec = Bool(v, p); break;
                case "desktop": b.Desktop = Str(v, p); break;
                case "web":
                    {
                        var wm = Map(v, p, "search", "scrape", "allow_hosts");
                        var w = new WebDef();
                        if (wm.TryGetValue("search", out var s)) w.Search = Bool(s, Join(p, "search"));
                        if (wm.TryGetValue("scrape", out var sc)) w.Scrape = Bool(sc, Join(p, "scrape"));
                        if (wm.TryGetValue("allow_hosts", out var ah)) w.AllowHosts = StrList(ah, Join(p, "allow_hosts"));
                        b.Web = w;
                        break;
                    }
                case "memory": b.Memory = ParseMemory(v, p); break;
                case "timezone": b.Timezone = Str(v, p); break;
                case "idle_sleep_minutes": b.IdleSleepMinutes = Int(v, p); break;
                case "limits": b.Limits = ParseLimits(v, p); break;
                case "routines": b.Routines = Seq(v, p).Select((n, i) => ParseRoutine(n, $"{p}[{i}]", topLevel: false)).ToList(); break;
                case "rooms": b.Rooms = StrList(v, p); break;
                case "accepts_from": b.AcceptsFrom = StrList(v, p); break;
                case "manages": b.Manages = StrList(v, p); break;
                case "keywords": b.Keywords = StrList(v, p); break;
                case "secrets": b.Secrets = ParseSecrets(v, p); break;
                case "auto_allow": b.AutoAllow = StrList(v, p); break;
                case "preset": b.Preset = Str(v, p); break;
            }
        }
        if (string.IsNullOrWhiteSpace(b.Name)) Error(path, b.Line, "name is required.");
        return b;
    }

    private Dictionary<string, SecretRef> ParseSecrets(YamlNode node, string path)
    {
        var result = new Dictionary<string, SecretRef>(StringComparer.Ordinal);
        if (node is not YamlMappingNode mapNode) { Error(path, Line(node), "Expected a mapping of secret names."); return result; }
        foreach (var (k, v) in mapNode.Children)
        {
            var name = (k as YamlScalarNode)?.Value ?? "";
            var p = Join(path, name);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][A-Z0-9_]{0,63}$"))
            {
                Error(p, Line(k), "Secret names use A to Z, 0 to 9 and underscores, starting with a letter.");
                continue;
            }
            if (v is YamlScalarNode)
            {
                Error(p, Line(v), "Secrets are only { file: PATH } or { env: NAME }. A literal value is refused and not shown.");
                continue;
            }
            var sm = Map(v, p, "file", "env");
            if (sm.Count != 1) { Error(p, Line(v), "Give exactly one of file or env."); continue; }
            var (kind, valNode) = sm.First();
            var val = Str(valNode, Join(p, kind)) ?? "";
            if (kind == "file" && !SafeRelativeOrAbsolute(val)) { Error(Join(p, kind), Line(valNode), "Secret file paths may not use ~, .. or . segments."); continue; }
            result[name] = new SecretRef(kind, val);
        }
        return result;
    }

    private RoutineDef ParseRoutine(YamlNode node, string path, bool topLevel)
    {
        var allowed = new List<string>
        {
            "name", "was", "cron", "interval_minutes", "at", "watch", "weekdays", "monthly", "yearly", "timezone", "checks",
            "instructions", "notify", "daily_token_budget", "enabled", "allow_external", "session", "host_exec",
        };
        if (topLevel) allowed.Add("bot");
        var map = Map(node, path, [.. allowed]);
        var r = new RoutineDef { Line = Line(node) };
        var presets = 0;
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "name": r.Name = Str(v, p) ?? ""; break;
                case "was": r.Was = Str(v, p); break;
                case "bot": r.Bot = Str(v, p); break;
                case "cron": r.Cron = Str(v, p); break;
                case "interval_minutes": r.IntervalMinutes = Int(v, p); break;
                case "at": r.At = Str(v, p); break;
                case "watch": r.Watch = Str(v, p); break;
                case "weekdays" or "monthly" or "yearly":
                {
                    presets++;
                    var text = Str(v, p) ?? "";
                    string? err;
                    var cron = key switch
                    {
                        "weekdays" => Routines.RoutineRules.WeekdaysCron(text, out err),
                        "monthly" => Routines.RoutineRules.MonthlyCron(text, out err),
                        _ => Routines.RoutineRules.YearlyCron(text, out err),
                    };
                    if (cron is null) Error(p, Line(v), err ?? "Not a valid preset.");
                    else if (r.Cron is not null) Error(p, Line(v), "Give exactly one trigger.");
                    else r.Cron = cron;
                    break;
                }
                case "timezone": r.Timezone = Str(v, p); break;
                case "checks": r.Checks = Seq(v, p).Select((n, i) => ParseCheck(n, $"{p}[{i}]")).ToList(); break;
                case "instructions": r.Instructions = Text(v, p, 12000, chars: true)?.Text ?? ""; break;
                case "notify": r.Notify = Str(v, p) ?? "errors_only"; break;
                case "daily_token_budget": r.DailyTokenBudget = Int(v, p); break;
                case "enabled": r.Enabled = Bool(v, p) ?? true; break;
                case "allow_external": r.AllowExternal = Bool(v, p) ?? false; break;
                case "session": r.Session = Str(v, p) ?? "fresh"; break;
                case "host_exec": r.HostExec = Str(v, p) ?? "off"; break;
            }
        }
        if (string.IsNullOrWhiteSpace(r.Name)) Error(path, r.Line, "name is required.");
        if (topLevel && string.IsNullOrWhiteSpace(r.Bot)) Error(path, r.Line, "bot is required for a top-level routine.");
        var cronGiven = map.ContainsKey("cron") ? 1 : 0;
        var triggers = cronGiven + presets + new[] { r.IntervalMinutes is not null, r.At is not null, r.Watch is not null }.Count(x => x);
        if (triggers != 1) Error(path, r.Line, "Give exactly one trigger: cron, interval_minutes, at, watch, weekdays, monthly or yearly.");
        if (r.Notify == "always")
            Error(Join(path, "notify"), r.Line, "notify: always is refused. Routines are quiet and notify only on errors (hard rule 8); use errors_only or never.");
        else if (r.Notify is not ("errors_only" or "never"))
            Error(Join(path, "notify"), r.Line, "notify must be errors_only or never.");
        if (r.Session is not ("fresh" or "standing")) Error(Join(path, "session"), r.Line, "session must be fresh or standing.");
        if (r.HostExec is not ("off" or "ask")) Error(Join(path, "host_exec"), r.Line, "host_exec must be off or ask. A routine never runs host commands without asking.");
        return r;
    }

    private static readonly string[] CheckKinds = ["file_changed", "git_new_commits", "http_status", "command", "assignments_waiting", "doctor", "always"];

    private CheckDef ParseCheck(YamlNode node, string path)
    {
        if (node is YamlScalarNode s && s.Value is "always" or "doctor" or "assignments_waiting") return new CheckDef { Kind = s.Value };
        var map = Map(node, path, CheckKinds);
        if (map.Count != 1) { Error(path, Line(node), "Each check has exactly one kind."); return new CheckDef(); }
        var (kind, args) = map.First();
        var c = new CheckDef { Kind = kind };
        if (args is YamlMappingNode am)
        {
            foreach (var (k, v) in am.Children)
            {
                var key = (k as YamlScalarNode)?.Value ?? "";
                c.Args[key] = Str(v, Join(Join(path, kind), key)) ?? "";
            }
        }
        else if (args is YamlScalarNode sv && !string.IsNullOrEmpty(sv.Value))
        {
            c.Args["value"] = sv.Value;
        }
        // The plan's example health routine runs `oma-bot doctor` as a command; that is the built-in doctor check.
        if (c.Kind == "command" && c.Args.TryGetValue("run", out var run) && run.TrimStart().StartsWith("oma-bot doctor", StringComparison.Ordinal))
            return new CheckDef { Kind = "doctor" };
        if (Routines.Checks.Problem(c) is { } problem) Error(Join(path, kind), Line(node), problem);
        return c;
    }

    private RouteDef ParseRoute(YamlNode node, string path)
    {
        var map = Map(node, path, "words", "to");
        var r = new RouteDef();
        if (map.TryGetValue("words", out var w)) r.Words = StrList(w, Join(path, "words"));
        if (map.TryGetValue("to", out var to)) r.To = Str(to, Join(path, "to")) ?? "";
        if (r.Words.Count == 0 || r.To.Length == 0) Error(path, Line(node), "A route needs words and to.");
        return r;
    }

    private RoomDef ParseRoom(YamlNode node, string path)
    {
        var map = Map(node, path, "name", "team", "members", "responder", "brief", "max_posts_per_hour");
        var r = new RoomDef { Line = Line(node) };
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "name": r.Name = Str(v, p) ?? ""; break;
                case "team": r.Team = Str(v, p); break;
                case "members": r.Members = StrList(v, p); break;
                case "responder": r.Responder = (Str(v, p) ?? "mentions") switch { "named" => "mentions", "all" => "everyone", var x => x }; break;
                case "brief": r.Brief = Text(v, p, MaxRoomBriefChars, chars: true); break;
                case "max_posts_per_hour": r.MaxPostsPerHour = Int(v, p) ?? 30; break;
            }
        }
        if (string.IsNullOrWhiteSpace(r.Name)) Error(path, r.Line, "name is required.");
        if (r.Responder is not ("mentions" or "lead" or "everyone"))
            Error(Join(path, "responder"), r.Line, "responder must be mentions, lead or everyone.");
        return r;
    }

    private MemoryDef ParseMemory(YamlNode node, string path)
    {
        var map = Map(node, path, "enabled", "max_lines", "max_bytes", "upkeep", "recall");
        var m = new MemoryDef();
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "enabled": m.Enabled = Bool(v, p); break;
                case "max_lines": m.MaxLines = Int(v, p); break;
                case "max_bytes": m.MaxBytes = Int(v, p); break;
                case "recall": m.Recall = Bool(v, p); break;
                case "upkeep":
                    var um = Map(v, p, "capture", "tidy");
                    if (um.TryGetValue("capture", out var c)) m.Capture = Bool(c, Join(p, "capture"));
                    if (um.TryGetValue("tidy", out var t)) m.Tidy = Bool(t, Join(p, "tidy"));
                    break;
            }
        }
        return m;
    }

    private LimitsDef ParseLimits(YamlNode node, string path)
    {
        var map = Map(node, path, "memory", "cpu", "max_turn_minutes", "daily_token_budget", "pids");
        var l = new LimitsDef();
        foreach (var (key, v) in map)
        {
            var p = Join(path, key);
            switch (key)
            {
                case "memory": l.Memory = Str(v, p); break;
                case "cpu": l.Cpu = Dbl(v, p); break;
                case "max_turn_minutes": l.MaxTurnMinutes = Int(v, p); break;
                case "daily_token_budget": l.DailyTokenBudget = Int(v, p); break;
                case "pids": l.Pids = Int(v, p); break;
            }
        }
        return l;
    }

    // ---- text and files ------------------------------------------------

    private TextValue? Text(YamlNode node, string path, int max, bool chars)
    {
        string text;
        string? source = null;
        if (node is YamlScalarNode s)
        {
            text = SecureFile.NormalizeLf(s.Value ?? "");
        }
        else
        {
            var map = Map(node, path, "file");
            if (!map.TryGetValue("file", out var f)) { Error(path, Line(node), "Expected text or { file: PATH }."); return null; }
            var rel = Str(f, Join(path, "file")) ?? "";
            if (!SafeRelativeOrAbsolute(rel) || Path.IsPathRooted(rel))
            {
                Error(Join(path, "file"), Line(f), "Use a path relative to the team file, without ~, .. or . segments.");
                return null;
            }
            var full = Path.GetFullPath(Path.Combine(_baseDir, rel));
            if (SecureFile.HasSymlinkSegment(full)) { Error(Join(path, "file"), Line(f), "The path has a symlink, which is refused."); return null; }
            if (!File.Exists(full)) { Error(Join(path, "file"), Line(f), "File not found: " + rel); return null; }
            if (new FileInfo(full).Length > max * 4L) { Error(path, Line(node), $"Over the size limit ({max})."); return null; }
            text = SecureFile.NormalizeLf(File.ReadAllText(full));
            source = rel;
            var secretLine = _redactor.FirstSecretLine(text);
            if (secretLine > 0)
                Error(Join(path, "file"), Line(f), $"{rel} line {secretLine} looks like a secret ({Redactor.Describe(text.Split('\n')[secretLine - 1])}). The value is not shown.");
        }
        var size = chars ? text.Length : System.Text.Encoding.UTF8.GetByteCount(text);
        if (size > max)
            Error(path, Line(node), chars ? $"Over {max} characters." : $"Over {max} bytes.");
        return new TextValue(text, source, Line(node));
    }

    internal static bool SafeRelativeOrAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('~')) return false;
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg is ".." or ".") return false;
        }
        return true;
    }

    // ---- node helpers --------------------------------------------------

    private Dictionary<string, YamlNode> Map(YamlNode node, string path, params string[] allowed)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        if (node is not YamlMappingNode mapNode)
        {
            Error(path.Length == 0 ? "(file)" : path, Line(node), "Expected a mapping.");
            return result;
        }
        foreach (var (k, v) in mapNode.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? "";
            if (Array.IndexOf(allowed, key) < 0)
            {
                Error(Join(path, key), Line(k), "Unknown field.");
                continue;
            }
            result[key] = v;
        }
        return result;
    }

    private List<YamlNode> Seq(YamlNode node, string path)
    {
        if (node is YamlSequenceNode seq) return [.. seq.Children];
        Error(path, Line(node), "Expected a list.");
        return [];
    }

    private string? Str(YamlNode node, string path)
    {
        if (node is YamlScalarNode s) return s.Value;
        Error(path, Line(node), "Expected a text value.");
        return null;
    }

    private List<string> StrList(YamlNode node, string path)
    {
        if (node is YamlScalarNode s && !string.IsNullOrEmpty(s.Value)) return [s.Value];
        return Seq(node, path).Select((n, i) => Str(n, $"{path}[{i}]") ?? "").Where(x => x.Length > 0).ToList();
    }

    private int? Int(YamlNode node, string path)
    {
        if (node is YamlScalarNode s && int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        Error(path, Line(node), "Expected a whole number.");
        return null;
    }

    private double? Dbl(YamlNode node, string path)
    {
        if (node is YamlScalarNode s && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        Error(path, Line(node), "Expected a number.");
        return null;
    }

    private bool? Bool(YamlNode node, string path)
    {
        if (node is YamlScalarNode s)
        {
            if (s.Value is "true" or "yes" or "on") return true;
            if (s.Value is "false" or "no" or "off") return false;
        }
        Error(path, Line(node), "Expected true or false.");
        return null;
    }

    private static int Line(YamlNode node) => (int)node.Start.Line;

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "." + b;

    private static string FirstLine(string s) => s.Split('\n')[0];

    private void Error(string path, int line, string message) => _errors.Add(new TeamError(path, line, message));
}
