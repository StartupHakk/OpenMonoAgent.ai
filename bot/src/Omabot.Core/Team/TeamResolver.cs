// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Config;

namespace Omabot.Team;

/// <summary>What the roster already knows, used to check a team file against live state.</summary>
public sealed record LiveState(IReadOnlyList<BotSpec> Bots, IReadOnlyCollection<string> KnownModels);

/// <summary>A resolved bot plus how it maps to an existing row.</summary>
public sealed record ResolvedBot(BotSpec Spec, BotSpec? Existing, string? RenamedFrom, int Line);

public sealed record ResolvedTeam(
    IReadOnlyList<ResolvedBot> Bots,
    IReadOnlyList<ModelDef> Models,
    IReadOnlyList<TeamDef> Teams,
    IReadOnlyList<RouteDef> Routes,
    IReadOnlyList<RoomDef> Rooms,
    IReadOnlyList<string> BlockedHosts,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies defaults, merges with existing bots (a field the file leaves out is
/// left as it is) and checks every rule in plan 7.2 and 7.13.
/// </summary>
public sealed class TeamResolver(RosterConfig config, RosterPaths paths)
{
    public static readonly string[] Profiles = ["ask", "edits", "auto"];
    public static readonly string[] Desktops = ["off", "shared", "own", "pool"];
    public static readonly string[] Roles = ["worker", "lead", "health"];

    private readonly List<TeamError> _errors = [];
    private readonly List<string> _warnings = [];

    public ResolvedTeam Resolve(TeamFile file, LiveState live)
    {
        _errors.Clear();
        _warnings.Clear();
        var knownModels = new HashSet<string>(live.KnownModels, StringComparer.OrdinalIgnoreCase);
        foreach (var m in file.Models) knownModels.Add(m.Nickname);

        // Names: unique in any case, and unique slugs.
        var seen = new Dictionary<string, BotDef>(StringComparer.OrdinalIgnoreCase);
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in file.Bots)
        {
            if (b.Name.Length > TeamParser.MaxNameLength) Err($"bots.{b.Name}", b.Line, $"Name is over {TeamParser.MaxNameLength} characters.");
            if (!seen.TryAdd(b.Name.Trim(), b)) Err($"bots.{b.Name}", b.Line, "Duplicate bot name (names ignore case).");
            var slug = Names.Slug(b.Name);
            if (slug.Length == 0) Err($"bots.{b.Name}", b.Line, "Name needs at least one letter or digit.");
            else if (!slugs.TryAdd(slug, b.Name)) Err($"bots.{b.Name}", b.Line, $"Name maps to the same folder as \"{slugs[slug]}\".");
        }

        var resolved = new List<ResolvedBot>();
        var leadCount = 0;
        foreach (var b in file.Bots)
        {
            BotSpec? existing = live.Bots.FirstOrDefault(x => Names.SameName(x.Name, b.Name));
            string? renamedFrom = null;
            if (b.Was is { } was)
            {
                var old = live.Bots.FirstOrDefault(x => Names.SameName(x.Name, was));
                if (old is not null)
                {
                    if (existing is not null && !ReferenceEquals(existing, old))
                    {
                        Err($"bots.{b.Name}.was", b.Line, $"Rename from \"{was}\" onto \"{b.Name}\" is refused: a bot with that name already exists.");
                        continue;
                    }
                    existing = old;
                    renamedFrom = old.Name;
                }
                else if (existing is null)
                {
                    _warnings.Add($"bot \"{b.Name}\": was \"{was}\" not found, treated as a new bot.");
                }
            }

            var spec = Merge(file, b, existing);
            if (spec.Role == "lead") leadCount++;
            Check(spec, b, existing, knownModels, file);
            resolved.Add(new ResolvedBot(spec, existing, renamedFrom, b.Line));
        }

        // Top-level routines attach to their bot.
        foreach (var r in file.Routines)
        {
            var target = resolved.FirstOrDefault(x => Names.SameName(x.Spec.Name, r.Bot ?? ""));
            if (target is null)
            {
                var liveBot = live.Bots.FirstOrDefault(x => Names.SameName(x.Name, r.Bot ?? ""));
                if (liveBot is null) { Err($"routines.{r.Name}.bot", r.Line, $"No bot named \"{r.Bot}\"."); continue; }
                var spec = liveBot.Clone();
                target = new ResolvedBot(spec, liveBot, null, r.Line);
                resolved.Add(target);
            }
            UpsertRoutine(target.Spec, ToRoutineSpec(r, target.Spec.Timezone), r.Was);
            CheckRoutine(target.Spec.Routines.Last(x => Names.SameName(x.Name, r.Name)), $"routines.{r.Name}", r.Line);
        }

        if (leadCount + live.Bots.Count(x => x.Role == "lead" && !resolved.Any(r => Names.SameName(r.Spec.Name, x.Name))) > 1)
            Err("bots", 0, "Only one bot may have role: lead (head-honcho).");

        CheckSharedWorkspaces(resolved, live);

        var allNames = new HashSet<string>(resolved.Select(r => r.Spec.Name).Concat(live.Bots.Select(x => x.Name)), StringComparer.OrdinalIgnoreCase);
        foreach (var r in resolved)
        {
            foreach (var a in r.Spec.AcceptsFrom.Where(a => a != "person" && !allNames.Contains(a)))
                Err($"bots.{r.Spec.Name}.accepts_from", r.Line, $"No bot named \"{a}\".");
            foreach (var m in r.Spec.Manages.Where(m => !allNames.Contains(m)))
                Err($"bots.{r.Spec.Name}.manages", r.Line, $"No bot named \"{m}\".");
            if (r.Spec.Manages.Any(m => Names.SameName(m, r.Spec.Name)))
                Err($"bots.{r.Spec.Name}.manages", r.Line, "A bot cannot manage itself.");
        }
        foreach (var t in file.Teams.Where(t => t.Lead is not null && !allNames.Contains(t.Lead!)))
            Err($"teams.{t.Name}.lead", t.Line, $"No bot named \"{t.Lead}\".");
        foreach (var route in file.Routes.Where(rt => !allNames.Contains(rt.To)))
            Err("routes", 0, $"Route target \"{route.To}\" is not a bot.");
        foreach (var room in file.Rooms)
        {
            foreach (var mem in room.Members.Where(mm => !allNames.Contains(mm)))
                Err($"rooms.{room.Name}.members", room.Line, $"No bot named \"{mem}\".");
        }

        if (_errors.Count > 0) throw new TeamFileException([.. _errors]);
        var blocked = file.BlockedHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList();
        return new ResolvedTeam(resolved, file.Models, file.Teams, file.Routes, file.Rooms, blocked, [.. _warnings]);
    }

    private BotSpec Merge(TeamFile file, BotDef b, BotSpec? existing)
    {
        var d = file.Defaults;
        var s = existing?.Clone() ?? new BotSpec();
        s.Name = b.Name.Trim();
        s.Slug = Names.Slug(s.Name);
        if (existing is null)
        {
            // New bot: built-in defaults, then file defaults.
            s.Model = d.Model ?? s.Model;
            s.PermissionProfile = d.PermissionProfile ?? s.PermissionProfile;
            s.Timezone = d.Timezone ?? s.Timezone;
            s.IdleSleepMinutes = d.IdleSleepMinutes ?? s.IdleSleepMinutes;
            s.HostExec = d.HostExec ?? s.HostExec;
            s.Desktop = d.Desktop ?? s.Desktop;
            s.MemoryMaxLines = config.Memory.MaxLines;
            s.MemoryMaxBytes = config.Memory.MaxBytes;
            ApplyMemory(s, d.Memory);
            ApplyLimits(s, d.Limits);
        }
        else
        {
            // Existing bot: defaults that the file states win over stored values for fields the bot entry leaves out.
            if (b.Model is null && d.Model is not null) s.Model = d.Model;
            if (b.PermissionProfile is null && d.PermissionProfile is not null) s.PermissionProfile = d.PermissionProfile;
            if (b.Timezone is null && d.Timezone is not null) s.Timezone = d.Timezone;
            if (b.IdleSleepMinutes is null && d.IdleSleepMinutes is not null) s.IdleSleepMinutes = d.IdleSleepMinutes.Value;
            if (b.HostExec is null && d.HostExec is not null) s.HostExec = d.HostExec.Value;
            if (b.Desktop is null && d.Desktop is not null) s.Desktop = d.Desktop;
            if (b.Memory is null) ApplyMemory(s, d.Memory);
            if (b.Limits is null) ApplyLimits(s, d.Limits);
        }
        if (b.Team is not null) s.Team = b.Team;
        if (b.Role is not null) s.Role = b.Role;
        if (b.Title is not null) s.Title = b.Title;
        if (b.Description is not null) s.Description = b.Description;
        if (b.SeenAs is not null) s.SeenAs = b.SeenAs;
        if (b.Personality is not null) s.Personality = b.Personality.Text;
        if (b.PlanPrompt is not null) s.PlanPrompt = b.PlanPrompt.Text;
        if (b.Instructions is not null) s.Instructions = b.Instructions.Text;
        if (b.Model is not null) s.Model = b.Model;
        if (b.PermissionProfile is not null) s.PermissionProfile = b.PermissionProfile;
        if (b.ToolsAllow is not null) s.ToolsAllow = Distinct(b.ToolsAllow);
        if (b.ToolsDeny is not null) s.ToolsDeny = Distinct(b.ToolsDeny);
        if (b.BashAllow is not null) s.BashAllow = Distinct(b.BashAllow);
        if (b.BashDeny is not null) s.BashDeny = Distinct(b.BashDeny);
        if (b.Workspace is not null) s.Workspace = b.Workspace.Trim();
        if (b.SharedWorkspace is not null) s.SharedWorkspace = b.SharedWorkspace.Value;
        if (b.HostExec is not null) s.HostExec = b.HostExec.Value;
        if (b.Desktop is not null) s.Desktop = b.Desktop;
        if (b.Web is not null)
        {
            if (b.Web.Search is not null) s.WebSearch = b.Web.Search.Value;
            if (b.Web.Scrape is not null) s.WebScrape = b.Web.Scrape.Value;
            if (b.Web.AllowHosts is not null) s.AllowHosts = Distinct(b.Web.AllowHosts.Select(h => h.Trim().ToLowerInvariant()));
        }
        ApplyMemory(s, b.Memory);
        if (b.Timezone is not null) s.Timezone = b.Timezone;
        if (b.IdleSleepMinutes is not null) s.IdleSleepMinutes = b.IdleSleepMinutes.Value;
        ApplyLimits(s, b.Limits);
        if (b.Rooms is not null) s.Rooms = Distinct(b.Rooms);
        if (b.AcceptsFrom is not null) s.AcceptsFrom = Distinct(b.AcceptsFrom);
        if (b.Manages is not null) s.Manages = Distinct(b.Manages);
        if (b.Keywords is not null) s.Keywords = Distinct(b.Keywords.Select(k => k.ToLowerInvariant()));
        if (b.Secrets is not null) s.Secrets = new SortedDictionary<string, string>(b.Secrets.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), StringComparer.Ordinal);
        if (b.Routines is not null)
        {
            foreach (var r in b.Routines) UpsertRoutine(s, ToRoutineSpec(r, s.Timezone), r.Was);
        }
        return s;
    }

    private static void ApplyMemory(BotSpec s, MemoryDef? m)
    {
        if (m is null) return;
        if (m.Enabled is not null) s.MemoryEnabled = m.Enabled.Value;
        if (m.MaxLines is not null) s.MemoryMaxLines = m.MaxLines.Value;
        if (m.MaxBytes is not null) s.MemoryMaxBytes = m.MaxBytes.Value;
        if (m.Capture is not null) s.MemoryCapture = m.Capture.Value;
        if (m.Tidy is not null) s.MemoryTidy = m.Tidy.Value;
        if (m.Recall is not null) s.Recall = m.Recall.Value;
    }

    private static void ApplyLimits(BotSpec s, LimitsDef? l)
    {
        if (l is null) return;
        if (l.Memory is not null) s.LimitMemory = l.Memory;
        if (l.Cpu is not null) s.LimitCpu = l.Cpu.Value;
        if (l.MaxTurnMinutes is not null) s.MaxTurnMinutes = l.MaxTurnMinutes.Value;
        if (l.DailyTokenBudget is not null) s.DailyTokenBudget = l.DailyTokenBudget.Value;
        if (l.Pids is not null) s.LimitPids = l.Pids.Value;
    }

    private static RoutineSpec ToRoutineSpec(RoutineDef r, string botZone) => new()
    {
        Name = r.Name,
        Cron = r.Cron,
        IntervalMinutes = r.IntervalMinutes,
        At = r.At,
        Watch = r.Watch,
        Timezone = r.Timezone ?? botZone,
        Checks = r.Checks,
        Instructions = r.Instructions,
        Notify = r.Notify,
        DailyTokenBudget = r.DailyTokenBudget ?? 50000,
        Enabled = r.Enabled,
        AllowExternal = r.AllowExternal,
    };

    private static void UpsertRoutine(BotSpec s, RoutineSpec r, string? was)
    {
        var idx = s.Routines.FindIndex(x => Names.SameName(x.Name, r.Name));
        if (idx < 0 && was is not null) idx = s.Routines.FindIndex(x => Names.SameName(x.Name, was));
        if (idx >= 0) s.Routines[idx] = r;
        else s.Routines.Add(r);
    }

    private void Check(BotSpec s, BotDef b, BotSpec? existing, HashSet<string> knownModels, TeamFile file)
    {
        var p = $"bots.{s.Name}";
        if (string.IsNullOrWhiteSpace(s.Personality)) Err(p + ".personality", b.Line, "personality is required.");
        if (s.Title is { Length: > TeamParser.MaxTitleLength }) Err(p + ".title", b.Line, $"Title is over {TeamParser.MaxTitleLength} characters.");
        if (!Profiles.Contains(s.PermissionProfile)) Err(p + ".permission_profile", b.Line, "permission_profile must be ask, edits or auto.");
        if (!Desktops.Contains(s.Desktop)) Err(p + ".desktop", b.Line, "desktop must be off, shared, own or pool.");
        if (!Roles.Contains(s.Role)) Err(p + ".role", b.Line, "role must be worker, lead or health.");
        if (string.IsNullOrWhiteSpace(s.Model)) Err(p + ".model", b.Line, "No model. Set model on the bot or in defaults.");
        else if (!knownModels.Contains(s.Model)) Err(p + ".model", b.Line, $"Model nickname \"{s.Model}\" is not known to the gateway.");
        if (!IsZone(s.Timezone)) Err(p + ".timezone", b.Line, $"\"{s.Timezone}\" is not an IANA time zone.");
        if (s.IdleSleepMinutes < 0) Err(p + ".idle_sleep_minutes", b.Line, "Cannot be negative.");
        if (s.MemoryMaxLines is < 10 or > 2000) Err(p + ".memory.max_lines", b.Line, "max_lines must be 10 to 2000.");
        if (s.MemoryMaxBytes is < 1024 or > 262144) Err(p + ".memory.max_bytes", b.Line, "max_bytes must be 1024 to 262144.");
        if (s.LimitCpu is <= 0 or > 64) Err(p + ".limits.cpu", b.Line, "cpu must be above 0 and at most 64.");
        if (s.LimitPids is < 64 or > 32768) Err(p + ".limits.pids", b.Line, "pids must be 64 to 32768.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.LimitMemory, "^[0-9]{2,6}[mg]$")) Err(p + ".limits.memory", b.Line, "memory must look like 1536m or 2g.");
        foreach (var t in s.ToolsAllow.Concat(s.ToolsDeny).Where(t => !ToolCatalog.IsKnown(t)))
            Err(p + ".tools", b.Line, $"Unknown tool \"{t}\".");
        foreach (var t in s.ToolsAllow.Where(ToolCatalog.IsExternalPost))
            Err(p + ".tools.allow", b.Line, $"\"{t}\" posts outside the box and is always denied for bots.");
        if (s.Role == "health")
        {
            foreach (var t in s.ToolsAllow.Where(t => !ToolCatalog.IsReadOnly(t)))
                Err(p + ".tools.allow", b.Line, $"The health bot is read only; \"{t}\" is not a read-only tool.");
            if (s.HostExec) Err(p + ".host_exec", b.Line, "The health bot cannot use host_exec.");
            if (s.PermissionProfile != "ask") Err(p + ".permission_profile", b.Line, "The health bot must use the ask profile.");
            if (s.Manages.Count > 0) Err(p + ".manages", b.Line, "The health bot cannot manage other bots.");
        }
        foreach (var h in s.AllowHosts)
        {
            if (file.BlockedHosts.Any(bh => h == bh || h.EndsWith("." + bh, StringComparison.Ordinal)) || config.Egress.BlockedHosts.Any(bh => h == bh || h.EndsWith("." + bh, StringComparison.Ordinal)))
                Err(p + ".web.allow_hosts", b.Line, $"\"{h}\" is a blocked host.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(h, @"^(\*\.)?[a-z0-9.-]+$")) Err(p + ".web.allow_hosts", b.Line, $"\"{h}\" is not a host name.");
        }
        CheckWorkspace(s, b, existing);
        for (var i = 0; i < s.Routines.Count; i++) CheckRoutine(s.Routines[i], $"{p}.routines.{s.Routines[i].Name}", b.Line);
    }

    private void CheckRoutine(RoutineSpec r, string path, int line)
    {
        if (!IsZone(r.Timezone)) Err(path + ".timezone", line, $"\"{r.Timezone}\" is not an IANA time zone.");
        if (r.IntervalMinutes is { } iv && (iv < Math.Max(5, config.Routines.MinIntervalMinutes) || iv > 1440))
            Err(path + ".interval_minutes", line, "interval_minutes must be 5 to 1440.");
        if (r.Cron is { } cron)
        {
            var err = Routines.CronRules.Validate(cron);
            if (err is not null) Err(path + ".cron", line, err);
        }
        if (r.At is { } at && !DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            Err(path + ".at", line, "at must be a local date and time like 2026-11-04 07:00.");
        if (r.Watch is { } w && w != "assignments" && !TeamParser.SafeRelativeOrAbsolute(w))
            Err(path + ".watch", line, "watch is a workspace path (no ~, .. or .) or assignments.");
    }

    private void CheckWorkspace(BotSpec s, BotDef b, BotSpec? existing)
    {
        var p = $"bots.{s.Name}.workspace";
        var line = b.WorkspaceLine > 0 ? b.WorkspaceLine : b.Line;
        var ws = s.Workspace;
        if (ws == "auto") return;
        if (ws.StartsWith('~')) { Err(p, line, "~ is refused. Use an absolute path under an allowed root."); return; }
        if (!Path.IsPathRooted(ws)) { Err(p, line, "Workspace must be auto or an absolute path."); return; }
        if (!TeamParser.SafeRelativeOrAbsolute(ws)) { Err(p, line, "A path with .. or . segments is refused, not resolved."); return; }
        var full = Path.GetFullPath(ws).TrimEnd('/');
        if (!config.WorkspaceRoots.Any(root => IsUnder(full, root)))
        {
            Err(p, line, $"Workspace is outside the allowed roots ({string.Join(", ", config.WorkspaceRoots)}).");
            return;
        }
        if (IsUnder(full, paths.Home)) { Err(p, line, "Workspace cannot be inside the roster home."); return; }
        if (!Directory.Exists(full)) { Err(p, line, "Workspace folder does not exist."); return; }
        if (SecureFile.HasSymlinkSegment(full)) Err(p, line, "The path has a symlink, which is refused.");
        _ = existing;
    }

    private void CheckSharedWorkspaces(List<ResolvedBot> resolved, LiveState live)
    {
        var all = resolved.Select(r => r.Spec).ToList();
        all.AddRange(live.Bots.Where(x => !resolved.Any(r => r.Existing is not null && Names.SameName(r.Existing.Name, x.Name)) && !resolved.Any(r => Names.SameName(r.Spec.Name, x.Name))));
        foreach (var group in all.Where(x => x.Workspace != "auto").GroupBy(x => Path.GetFullPath(x.Workspace).TrimEnd('/'), StringComparer.Ordinal))
        {
            var list = group.ToList();
            if (list.Count > 1 && list.Any(x => !x.SharedWorkspace))
                Err($"bots.{list[0].Name}.workspace", 0, $"Workspace {group.Key} is used by {string.Join(", ", list.Select(x => x.Name))}; every one of them must set shared_workspace: true.");
        }
    }

    public static bool IsUnder(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd('/');
        var full = Path.GetFullPath(path).TrimEnd('/');
        return full == r || full.StartsWith(r + "/", StringComparison.Ordinal);
    }

    public static bool IsZone(string zone)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); return zone.Contains('/') || zone == "UTC"; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static List<string> Distinct(IEnumerable<string> items) => items.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private void Err(string path, int line, string message) => _errors.Add(new TeamError(path, line, message));
}
