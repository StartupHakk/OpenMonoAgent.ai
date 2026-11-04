// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Acp;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Doctor;
using Omabot.Render;
using Omabot.Supervision;
using Omabot.Team;

namespace Omabot.Cli;

/// <summary>Exit codes from plan 7.13.</summary>
public static class Exit
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Invalid = 2;
    public const int NeedsInput = 3;
    public const int TurnFailed = 4;
    public const int TimedOut = 5;
    public const int WouldChange = 6;
}

/// <summary>The oma-bot command line. Commands added by later phases register in <see cref="Commands"/>.</summary>
public sealed partial class CliApp(CliEnv env)
{
    public const string Version = "0.1.0";

    private delegate Task<int> Command(Args a, CancellationToken ct);

    private Dictionary<string, (Command Run, string Help)> Commands => new(StringComparer.Ordinal)
    {
        ["version"] = ((_, _) => { env.Out.WriteLine("oma-bot " + Version); return Task.FromResult(Exit.Ok); }, "Print the version."),
        ["init"] = (Init, "Create ~/.omabot with roster.json and a starter team.yaml."),
        ["team"] = (Team, "team apply FILE [--dry-run|--check] | team export [--out FILE]"),
        ["up"] = (Up, "up [BOT]: start every enabled bot, or one."),
        ["down"] = (Down, "down [BOT]: stop every bot, or one."),
        ["status"] = (Status, "status [BOT]: state, port, last error."),
        ["run"] = (RunTurn, "run BOT \"message\" [--json] [--mode plan|build] [--wait SECONDS] [--wait-done SECONDS] [--timeout MINUTES]: one turn."),
        ["assignments"] = (AssignmentsCmd, "assignments [list] [--all] | show ID | cancel ID [--reason TEXT]"),
        ["events"] = (Events, "events BOT [--lines N]: show the bot's recent turn events."),
        ["doctor"] = (DoctorCmd, "doctor [--json] [--quiet] [--online]: health check."),
        ["enable"] = (Enable, "enable BOT: re-enable a disabled bot."),
        ["disable"] = (Disable, "disable BOT: stop and disable a bot."),
        ["notices"] = (NoticesCmd, "notices [--seen ID]: unseen roster notices."),
        ["audit"] = (AuditCmd, "audit tail [N] | audit verify | audit tree ROOT_ID"),
        ["serve"] = (ServeAsync, "serve [--once]: run the gateway and health monitor (the roster service)."),
        ["gateway"] = (GatewayCmd, "gateway status | slots [--backend NAME] | bench [--backend NAME] [--levels 1,2,4] [--save]"),
        ["secrets"] = (SecretsCmd, "secrets set NAME [--bot BOT] [--file PATH] (value on stdin) | list [--bot BOT] | rm NAME [--bot BOT] | rotate-bot BOT"),
        ["usage"] = (UsageCmd, "usage [--bot BOT] [--days N] [--json]: model calls and tokens per bot and routine."),
        ["memory"] = (MemoryCmd, "memory show|journal [--lines N]|undo ID|forget \"fact\" [--topic T]|scan|tidy BOT"),
        ["render"] = (RenderCmd, "render BOT: write the bot's files without starting it (prints file paths)."),
        ["routines"] = (RoutinesCmd, "routines list [BOT] | runs [BOT] [ROUTINE] [--lines N] | next|run-now|pause|resume BOT ROUTINE"),
        ["approvals"] = (ApprovalsCmd, "approvals [--all] | approvals show ID | approvals --answer ID allow|deny [--reason TEXT] [--hash H] [--text ANSWER]"),
        ["bot"] = (BotCmd, "bot history BOT | bot undo BOT VERSION_ID: bot spec versions and undo."),
        ["egress"] = (EgressCmd, "egress status | rules | check | log [--lines N] [--refused]: the egress gate and firewall rules."),
        ["desktops"] = (DesktopsCmd, "desktops [list] | show NAME | stop NAME: optional bot desktops."),
        ["room"] = (RoomCmd, "room list | show ROOM [--lines N] | post ROOM \"message\": room threads."),
        ["web"] = (WebCmd, "web open | enable [--public-origin URL] | disable | status | admin-secret [--rotate]: the web view and web mode."),
        ["fleet"] = (FleetCmd, "fleet enable --by NAME --confirm | disable | status | invite NODE [--labels k=v,..] [--never-touch] | approve NODE --fingerprint F | pause | resume | drain NODE | revoke NODE | job add FILE | job show|cancel ID | jobs | secret set NAME: optional fleet mode, run by a person only."),
        ["release"] = (ReleaseCmd, "release check [--repo DIR] [--advisories FILE | --online] [--multi]: the security release gate's automatic checks (plan 11.8)."),
        ["images"] = (ImagesCmd, "images [status] | accept [--digest sha256:...]: the accepted agent image; a changed image never starts until accepted."),
        ["users"] = (UsersCmd, "users add NAME --role owner|operator|viewer [--home ROSTER] | confirm NAME | list | homes | role NAME ROLE | disable|enable|logout|password NAME: web accounts (passwords and codes on stdin)."),
    };

    private Task<int> RoomCmd(Args a, CancellationToken ct)
    {
        var sub = a.NextOrNull() ?? "list";
        var config = LoadConfig();
        var (db, audit, notices, _) = OpenDb();
        var runtime = new Runtime.AssignmentRuntime(db, config, env.Paths, env.Clock, audit, notices, new Runtime.TurnDriver(db, env.Clock, env.Paths, env.Acp));
        var rooms = new Rooms.RoomService(db, env.Clock, audit, runtime);
        switch (sub)
        {
            case "list":
                a.NoMore();
                if (rooms.Rooms().Count == 0) env.Out.WriteLine("No rooms. Add rooms: to the team file.");
                foreach (var r in rooms.Rooms()) env.Out.WriteLine($"{r.Name,-16} {r.Responder,-9} {string.Join(", ", r.Members)}");
                return Task.FromResult(Exit.Ok);
            case "show":
                {
                    var name = a.Next("room show ROOM");
                    var n = int.TryParse(a.Option("--lines"), out var lines) ? Math.Clamp(lines, 1, 500) : 30;
                    a.NoMore();
                    var room = rooms.Find(name) ?? throw new ArgsException($"No room {name}.");
                    foreach (var m in rooms.Messages(room.Name, 0, n)) env.Out.WriteLine($"[{m.Id}] {m.At[..16]} {m.Author} ({m.AuthorKind}): {m.Text}");
                    foreach (var t in rooms.Turns(room.Name).Where(t => t.Status is "queued" or "running")) env.Out.WriteLine($"  {t.Bot} {t.Status} (hop {t.Hop})");
                    return Task.FromResult(Exit.Ok);
                }
            case "post":
                {
                    var name = a.Next("room post ROOM \"message\"");
                    var text = a.Next("room post ROOM \"message\"");
                    a.NoMore();
                    var (ok, message, _) = rooms.PostByPerson(name, text, PersonAtCli.ToString());
                    (ok ? env.Out : env.Err).WriteLine(message + (ok ? " The roster service runs the replies." : ""));
                    return Task.FromResult(ok ? Exit.Ok : Exit.Failed);
                }
            default:
                throw new ArgsException("Use: oma-bot room list | show ROOM [--lines N] | post ROOM \"message\"");
        }
    }

    private Task<int> BotCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("bot history|undo BOT");
        var name = a.Next("bot " + sub + " BOT");
        var config = LoadConfig();
        var (db, audit, _, bots) = OpenDb();
        var row = bots.Find(name) ?? throw new ArgsException($"No bot named \"{name}\".");
        switch (sub)
        {
            case "history":
                {
                    a.NoMore();
                    var versions = new BotVersions(db, env.Clock).For(row.Slug);
                    if (versions.Count == 0) env.Out.WriteLine($"{row.Name} has no recorded versions yet.");
                    foreach (var v in versions)
                        env.Out.WriteLine($"{v.Id,6}  {v.At}  {v.SpecHash[..12]}  {v.ChangedBy,-24} {v.Reason}{(v.SpecHash == row.Spec.Hash() ? "  (current)" : "")}");
                    return Task.FromResult(Exit.Ok);
                }
            case "undo":
                {
                    var id = long.TryParse(a.Next("bot undo BOT VERSION_ID"), out var n) ? n : throw new ArgsException("VERSION_ID is a number from oma-bot bot history.");
                    a.NoMore();
                    var r = BotChangesFor(config, db, audit, new Approvals.ApprovalStore(db, env.Clock, Redactor.Shared)).Undo(row.Name, id, PersonAtCli.ToString());
                    (r.Ok ? env.Out : env.Err).WriteLine(r.Message);
                    return Task.FromResult(r.Ok ? Exit.Ok : Exit.Failed);
                }
            default:
                throw new ArgsException("Use: oma-bot bot history BOT | bot undo BOT VERSION_ID");
        }
    }

    public async Task<int> RunAsync(string[] argv, CancellationToken ct = default)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
        {
            env.Out.WriteLine("Usage: oma-bot COMMAND [args]\n");
            foreach (var (name, c) in Commands.OrderBy(k => k.Key, StringComparer.Ordinal)) env.Out.WriteLine($"  {name,-10} {c.Help}");
            return Exit.Ok;
        }
        if (!Commands.TryGetValue(argv[0], out var cmd))
        {
            env.Err.WriteLine($"Unknown command \"{argv[0]}\". Run oma-bot help.");
            return Exit.Invalid;
        }
        try
        {
            return await cmd.Run(new Args(argv[1..]), ct).ConfigureAwait(false);
        }
        catch (ArgsException ex)
        {
            env.Err.WriteLine(ex.Message);
            return Exit.Invalid;
        }
        catch (TeamFileException ex)
        {
            foreach (var e in ex.Errors) env.Err.WriteLine(e.ToString());
            return Exit.Invalid;
        }
    }

    // ---------- shared helpers ----------

    private RosterConfig LoadConfig()
    {
        if (!File.Exists(env.Paths.ConfigFile)) throw new ArgsException($"No roster at {env.Paths.Home}. Run oma-bot init first.");
        return RosterConfig.Load(env.Paths);
    }

    private (RosterDb Db, AuditLog Audit, Notices Notices, BotStore Bots) OpenDb()
    {
        SecureFile.EnsureDirNoClobber(env.Paths.Home);
        SecureFile.EnsureDirNoClobber(env.Paths.Logs);
        var db = new RosterDb(env.Paths.Db);
        return (db, new AuditLog(db, env.Clock, Redactor.Shared, env.Paths.Logs), new Notices(db, env.Clock, Redactor.Shared), new BotStore(db, env.Clock));
    }

    private BotLauncher Launcher(RosterConfig config, RosterDb db, AuditLog audit, Notices notices, ISupervisorClient sup)
        => new(db, config, env.Paths, env.Clock, audit, notices, sup, env.Acp, env.Launch ?? LaunchOptions.Default(env.AppDir))
        {
            EgressProxyFor = slug => config.Egress.Enabled && new BotStore(db, env.Clock).Find(slug) is { Spec.IsReadOnlyRole: false }
                ? new Egress.EgressClients(env.Paths, config, Redactor.Shared).ProxyUrl(Egress.EgressClients.Agent(slug))
                : null,
        };

    private ISupervisorClient RequireSupervisor()
        => env.Supervisor() ?? throw new ArgsException("The supervisor is not running. Start it with: oma-bot-supervisor (see bot/README.md).");

    // ---------- commands ----------

    private Task<int> Init(Args a, CancellationToken ct)
    {
        a.NoMore();
        var p = env.Paths;
        SecureFile.EnsureDirNoClobber(p.Home);
        foreach (var d in new[] { p.Secrets, p.BotSecretsRoot, p.Logs, p.Run, p.Bots, p.Archive }) SecureFile.EnsureDirNoClobber(d);
        if (!File.Exists(p.ConfigFile))
        {
            new RosterConfig().Save(p);
            env.Out.WriteLine($"Wrote {p.ConfigFile}. Set gateway.upstream to the relay address and put the inference key in {p.InferenceKey} (mode 0600).");
        }
        else env.Out.WriteLine($"{p.ConfigFile} already exists; left as it is.");
        var teamDir = Path.Combine(p.Home, "team");
        SecureFile.EnsureDirNoClobber(teamDir);
        SecureFile.EnsureDirNoClobber(Path.Combine(teamDir, "bots"));
        var team = Path.Combine(teamDir, "team.yaml");
        SecureFile.WriteIfMissing(Path.Combine(teamDir, "bots", "head-honcho.md"), Templates.Get("bots/head-honcho.md"));
        SecureFile.WriteIfMissing(Path.Combine(teamDir, "bots", "health.md"), Templates.Get("bots/health.md"));
        if (SecureFile.WriteIfMissing(team, Templates.Get("team.default.yaml")))
            env.Out.WriteLine($"Wrote {team}. Review it, then run: oma-bot team apply {team}");
        if (!File.Exists(p.SupervisorToken)) SecureFile.WriteIfMissing(p.SupervisorToken, SecureFile.NewToken() + "\n");
        new AuditLog(new RosterDb(p.Db), env.Clock, Redactor.Shared, p.Logs).Write("person", "roster.init", p.Home, "");
        return Task.FromResult(Exit.Ok);
    }

    private Task<int> Team(Args a, CancellationToken ct)
    {
        var sub = a.Next("team apply FILE | team export");
        var config = LoadConfig();
        var (db, audit, _, bots) = OpenDb();
        var planner = new TeamPlanner(db, config, env.Paths, env.Clock, audit);
        if (sub == "export")
        {
            var outFile = a.Option("--out");
            a.NoMore();
            var text = new TeamExporter(db, bots).Export();
            if (outFile is null) env.Out.Write(text);
            else
            {
                SecureFile.WriteAllTextAtomic(outFile, text);
                env.Out.WriteLine($"Wrote {outFile}.");
            }
            return Task.FromResult(Exit.Ok);
        }
        if (sub != "apply") throw new ArgsException("Use: oma-bot team apply FILE [--dry-run|--check] or oma-bot team export [--out FILE]");
        var dry = a.Flag("--dry-run");
        var check = a.Flag("--check");
        var file = a.Next("team apply FILE");
        a.NoMore();
        var parsed = TeamParser.ParseFile(file);
        var plan = planner.Plan(parsed);
        env.Out.Write(plan.Render(env.Clock));
        if (check) return Task.FromResult(plan.HasChanges ? Exit.WouldChange : Exit.Ok);
        if (dry || !plan.HasChanges) return Task.FromResult(Exit.Ok);
        var renderer = new BotRenderer(env.Paths, config);
        var result = planner.Apply(plan, "person", spec => renderer.EnsureFolders(spec));
        foreach (var d in result.Done) env.Out.WriteLine(d);
        if (!result.Ok)
        {
            env.Err.WriteLine("Stopped: " + result.Error);
            return Task.FromResult(Exit.Failed);
        }
        // First apply renders the files so the hard rules exist before any start (INT-10).
        var launcher = new BotLauncher(db, config, env.Paths, env.Clock, audit, new Notices(db, env.Clock, Redactor.Shared),
            new NullSupervisor(), env.Acp, env.Launch ?? LaunchOptions.Default(env.AppDir));
        foreach (var row in bots.All())
        {
            var rendered = renderer.Render(row.Spec, launcher.ContextFor(row));
            bots.SetRenderedHash(row.Slug, rendered.OpenMonoHash);
        }
        env.Out.WriteLine("Applied.");
        return Task.FromResult(Exit.Ok);
    }

    private async Task<int> Up(Args a, CancellationToken ct)
    {
        var name = a.NextOrNull();
        a.NoMore();
        var config = LoadConfig();
        var (db, audit, notices, bots) = OpenDb();
        var launcher = Launcher(config, db, audit, notices, RequireSupervisor());
        var targets = name is null ? bots.All().Where(b => b.Enabled).Select(b => b.Name).ToList() : [name];
        var ok = true;
        foreach (var t in targets)
        {
            var r = await launcher.UpAsync(t, ct).ConfigureAwait(false);
            (r.Ok ? env.Out : env.Err).WriteLine(r.Message);
            ok &= r.Ok;
        }
        return ok ? Exit.Ok : Exit.Failed;
    }

    private async Task<int> Down(Args a, CancellationToken ct)
    {
        var name = a.NextOrNull();
        a.NoMore();
        var config = LoadConfig();
        var (db, audit, notices, bots) = OpenDb();
        var launcher = Launcher(config, db, audit, notices, RequireSupervisor());
        var targets = name is null ? bots.All().Select(b => b.Name).ToList() : [name];
        var ok = true;
        foreach (var t in targets)
        {
            var r = await launcher.DownAsync(t, ct).ConfigureAwait(false);
            (r.Ok ? env.Out : env.Err).WriteLine(r.Message);
            ok &= r.Ok;
        }
        return ok ? Exit.Ok : Exit.Failed;
    }

    private Task<int> Status(Args a, CancellationToken ct)
    {
        var name = a.NextOrNull();
        var json = a.Flag("--json");
        a.NoMore();
        var (_, _, notices, bots) = OpenDb();
        var rows = name is null ? bots.All() : [bots.Get(name)];
        if (json)
        {
            env.Out.WriteLine(JsonSerializer.Serialize(rows.Select(r => new { name = r.Name, slug = r.Slug, state = r.State, enabled = r.Enabled, port = r.Port, model = r.Spec.Model, role = r.Spec.Role, last_error = r.LastError, last_active_at = r.LastActiveAt })));
            return Task.FromResult(Exit.Ok);
        }
        if (rows.Count == 0) env.Out.WriteLine("No bots yet. Run oma-bot team apply FILE.");
        foreach (var r in rows)
        {
            env.Out.Write($"{r.Name,-20} {r.State,-10} 127.0.0.1:{r.Port}  {r.Spec.Model}  {r.Spec.Role}");
            if (!r.Enabled) env.Out.Write($"  disabled: {r.DisabledReason}");
            else if (r.State == BotStates.Failed && r.LastError is not null) env.Out.Write($"  last error: {r.LastError}");
            env.Out.WriteLine();
        }
        var n = notices.Unseen().Count;
        if (n > 0) env.Out.WriteLine($"{n} unseen notice(s). Run oma-bot notices.");
        return Task.FromResult(Exit.Ok);
    }

    private async Task<int> RunTurn(Args a, CancellationToken ct)
    {
        var json = a.Flag("--json");
        var timeout = a.Option("--timeout");
        var waitSeconds = int.Parse(a.Option("--wait") ?? "120", System.Globalization.CultureInfo.InvariantCulture);
        int? waitDone = a.Option("--wait-done") is { } wds ? int.Parse(wds, System.Globalization.CultureInfo.InvariantCulture) : null;
        var mode = a.Option("--mode");
        if (mode is not (null or "plan" or "build")) throw new ArgsException("--mode is plan or build.");
        var name = a.Next("run BOT \"message\"");
        var message = a.NextOrNull() ?? env.ReadStdin() ?? throw new ArgsException("Give a message: oma-bot run BOT \"message\"");
        a.NoMore();
        var (db, audit, notices, bots) = OpenDb();
        var row = bots.Get(name);
        var original = message;
        try
        {
            message = PrepareMemoryTurn(Launcher(LoadConfig(), db, audit, notices, NullSupervisor.Instance), row, message, personTurn: true);
        }
        catch (IOException ex)
        {
            env.Err.WriteLine("Note: memory could not be refreshed: " + Redactor.Shared.Redact(ex.Message));
        }
        if (row.State != BotStates.Running) env.Err.WriteLine($"Note: {row.Name} is {row.State}. Run oma-bot up {row.Name} if this fails.");
        var limit = TimeSpan.FromMinutes(timeout is null ? row.Spec.MaxTurnMinutes : int.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture));
        var contexts = new Gateway.TurnContexts(db, env.Clock, Gateway.Priority.Assignment);
        var ttl = TimeSpan.FromMinutes(row.Spec.MaxTurnMinutes + 1);
        var lease = contexts.TryAcquire(row.Slug, Gateway.Priority.Interactive, null, ttl);
        if (lease is null)
        {
            env.Err.WriteLine($"Note: {row.Name} is busy with another roster turn; waiting up to {waitSeconds} seconds.");
            lease = await contexts.AcquireAsync(row.Slug, Gateway.Priority.Interactive, null, ttl, TimeSpan.FromSeconds(waitSeconds), TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (lease is null)
            {
                env.Err.WriteLine($"{row.Name} is still busy. Try again later, or pass --wait SECONDS.");
                return Exit.TurnFailed;
            }
        }
        // A person's message is a root assignment (origin person), so work the bot hands out has a parent to report to.
        var config = LoadConfig();
        var runtime = new Runtime.AssignmentRuntime(db, config, env.Paths, env.Clock, audit, notices, new Runtime.TurnDriver(db, env.Clock, env.Paths, env.Acp));
        runtime.Pauses = PauseHandlerFactory(runtime);
        Runtime.TurnReport? report;
        Runtime.AssignmentRow final;
        try
        {
            var e = runtime.Enqueue(new Runtime.EnqueueRequest(To: row.Slug, Mode: "work", Brief: original, Rework: true, Origin: "person", RequesterKind: "person",
                Session: "standing", Priority: Gateway.Priority.Interactive, Actor: PersonAtCli.ToString(), ClaimWith: lease, TurnMessage: message));
            if (!e.Ok || e.Id is null)
            {
                env.Err.WriteLine($"Not started ({e.Reason}): {e.Message}");
                return Exit.TurnFailed;
            }
            (report, final) = await runtime.RunNowAsync(e.Id, lease, mode, ev =>
            {
                if (json) env.Out.WriteLine(JsonSerializer.Serialize(new { @event = ev.Name, data = Redactor.Shared.Redact(ev.Data) }));
            }, ct, limit).ConfigureAwait(false);
        }
        finally
        {
            contexts.Release(lease);
        }
        var text = report?.Text ?? "";
        bots.Touch(row.Slug);
        audit.Write(PersonAtCli.ToString(), "bot.turn", row.Name, $"{final.Id} {final.Status} tools {report?.Evidence.Tools.Count ?? 0}");
        AfterMemoryTurn(db, row, original, text, report?.End == TurnEnd.Done, $"{final.Status}, {report?.Evidence.Tools.Count ?? 0} tool call(s)");
        if (!json && text.Length > 0) env.Out.WriteLine(text);
        if (report?.TimedOut == true)
        {
            env.Err.WriteLine($"{row.Name} did not finish in time; the turn was aborted.");
            return Exit.TimedOut;
        }
        switch (final.Status)
        {
            case Runtime.AssignmentStatus.Completed:
                return Exit.Ok;
            case Runtime.AssignmentStatus.Waiting:
                env.Err.WriteLine($"{row.Name} handed out work ({final.Id}). The roster service resumes it when that work finishes; follow it with: oma-bot audit tree {final.RootId}");
                return waitDone is { } wd ? await WaitForRootAsync(runtime, final.RootId, TimeSpan.FromSeconds(wd), ct).ConfigureAwait(false) : Exit.Ok;
            case Runtime.AssignmentStatus.NeedsInput:
                {
                    var card = runtime.Approvals.Pending().FirstOrDefault(p => p.AssignmentId == final.Id);
                    env.Err.WriteLine(card is null
                        ? $"{row.Name} is waiting for an answer: {final.OutcomeDetail ?? report?.Pause?.Summary ?? "see oma-bot approvals"}. Answer with oma-bot approvals."
                        : $"{card.Summary}\nAnswer within {Math.Max(0, (card.ExpiresAt - env.Clock.UtcNow).TotalMinutes):0} minutes: oma-bot approvals --answer {card.Id} allow|deny. The roster service delivers it.");
                    return Exit.NeedsInput;
                }
            default:
                env.Err.WriteLine($"Turn failed: {final.OutcomeDetail ?? report?.Error ?? final.Status}");
                return Exit.TurnFailed;
        }
    }

    /// <summary>`oma-bot run --wait-done SECONDS`: follows a root that handed out work until it settles.</summary>
    private async Task<int> WaitForRootAsync(Runtime.AssignmentRuntime runtime, string rootId, TimeSpan wait, CancellationToken ct)
    {
        var deadline = env.Clock.UtcNow + wait;
        while (env.Clock.UtcNow < deadline)
        {
            var root = runtime.Store.Get(rootId)!;
            if (root.IsTerminal)
            {
                if (!string.IsNullOrEmpty(root.Result)) env.Out.WriteLine(root.Result);
                return root.Status == Runtime.AssignmentStatus.Completed ? Exit.Ok : Exit.TurnFailed;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        env.Err.WriteLine($"{rootId} is still going. Check later with: oma-bot assignments show {rootId}");
        return Exit.TimedOut;
    }

    /// <summary>The pause handler the CLI's runtime uses: the approval broker (plan 7.6).</summary>
    internal Func<Runtime.AssignmentRuntime, Runtime.IPauseHandler> PauseHandlerFactory { get; init; } = rt => new Approvals.ApprovalBroker(rt);

    private Task<int> Events(Args a, CancellationToken ct)
    {
        var lines = int.Parse(a.Option("--lines") ?? "50", System.Globalization.CultureInfo.InvariantCulture);
        var name = a.Next("events BOT");
        a.NoMore();
        var (_, _, _, bots) = OpenDb();
        var row = bots.Get(name);
        var log = Path.Combine(env.Paths.Logs, $"events-{row.Slug}.log");
        if (!File.Exists(log)) env.Out.WriteLine($"No events for {row.Name} yet.");
        else foreach (var l in File.ReadLines(log).TakeLast(lines)) env.Out.WriteLine(l);
        return Task.FromResult(Exit.Ok);
    }

    private async Task<int> DoctorCmd(Args a, CancellationToken ct)
    {
        var json = a.Flag("--json");
        var quiet = a.Flag("--quiet");
        var online = a.Flag("--online");
        a.NoMore();
        var results = await new DoctorChecks(env.Paths, env.Clock).RunAsync(
            new DoctorOptions(env.AppDir, !online, env.Supervisor(), CliEnv.SharedHttp, env.HttpsHandler()), ct).ConfigureAwait(false);
        env.Out.Write(json ? DoctorChecks.ToJson(results) + "\n" : DoctorChecks.Format(results, quiet));
        return DoctorChecks.ExitCode(results);
    }

    private Task<int> Enable(Args a, CancellationToken ct)
    {
        var name = a.Next("enable BOT");
        a.NoMore();
        var (db, audit, _, bots) = OpenDb();
        var row = bots.Get(name);
        bots.Enable(row.Slug);
        db.Exec("DELETE FROM bot_failures WHERE bot_id=$b;", ("$b", row.Id));
        audit.Write("person", "bot.enable", row.Name, "");
        env.Out.WriteLine($"{row.Name} is enabled. Run oma-bot up {row.Name}.");
        return Task.FromResult(Exit.Ok);
    }

    private async Task<int> Disable(Args a, CancellationToken ct)
    {
        var name = a.Next("disable BOT");
        a.NoMore();
        var (_, audit, _, bots) = OpenDb();
        var row = bots.Get(name);
        if (env.Supervisor() is { } sup) await sup.StopAsync("oma-bot-" + row.Slug, ct).ConfigureAwait(false);
        bots.Disable(row.Slug, "disabled by person");
        audit.Write("person", "bot.disable", row.Name, "");
        env.Out.WriteLine($"{row.Name} is disabled.");
        return Exit.Ok;
    }

    private Task<int> NoticesCmd(Args a, CancellationToken ct)
    {
        var seen = a.Option("--seen");
        a.NoMore();
        var (_, _, notices, _) = OpenDb();
        if (seen is not null)
        {
            notices.MarkSeen(long.Parse(seen, System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(Exit.Ok);
        }
        var list = notices.Unseen();
        if (list.Count == 0) env.Out.WriteLine("No unseen notices.");
        foreach (var n in list) env.Out.WriteLine($"{n.Id,5} {n.At} {n.Level,-6} {n.Bot ?? "-",-16} {n.Text}");
        return Task.FromResult(Exit.Ok);
    }

    private Task<int> AuditCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("audit tail [N] | audit verify | audit tree ROOT_ID");
        if (sub == "tree")
        {
            var root = a.Next("audit tree ROOT_ID");
            a.NoMore();
            return Task.FromResult(AuditTree(root));
        }
        var (_, audit, _, _) = OpenDb();
        if (sub == "verify")
        {
            a.NoMore();
            var broken = audit.Verify();
            env.Out.WriteLine(broken is null ? "Audit hash chain is intact." : $"Broken at row {broken.Value.Id}: {broken.Value.Reason}");
            return Task.FromResult(broken is null ? Exit.Ok : Exit.Failed);
        }
        if (sub != "tail") throw new ArgsException("Use: oma-bot audit tail [N] or oma-bot audit verify");
        var n = int.Parse(a.NextOrNull() ?? "20", System.Globalization.CultureInfo.InvariantCulture);
        a.NoMore();
        foreach (var r in audit.Tail(n)) env.Out.WriteLine($"{r.Id,6} {r.At} {r.Actor,-12} {r.Action,-20} {r.Subject} {r.Detail}");
        return Task.FromResult(Exit.Ok);
    }

    private Task<int> RenderCmd(Args a, CancellationToken ct)
    {
        var name = a.Next("render BOT");
        a.NoMore();
        var config = LoadConfig();
        var (db, audit, notices, bots) = OpenDb();
        var row = bots.Get(name);
        var launcher = Launcher(config, db, audit, notices, new NullSupervisor());
        var renderer = new BotRenderer(env.Paths, config);
        var rendered = renderer.Render(row.Spec, launcher.ContextFor(row));
        bots.SetRenderedHash(row.Slug, rendered.OpenMonoHash);
        var bp = env.Paths.Bot(row.Slug);
        env.Out.WriteLine(bp.Settings);
        env.Out.WriteLine(bp.SystemPrompt);
        env.Out.WriteLine(Path.Combine(renderer.WorkspaceOf(row.Spec), "OPENMONO.md"));
        return Task.FromResult(Exit.Ok);
    }
}

/// <summary>Used where a launcher is needed only for rendering.</summary>
internal sealed class NullSupervisor : ISupervisorClient
{
    public static readonly NullSupervisor Instance = new();

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<string> EnsureRunningAsync(Containers.ContainerSpec spec, CancellationToken ct = default) => throw new InvalidOperationException("No supervisor.");
    public Task StopAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Containers.ContainerInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Containers.ContainerInfo>>([]);
    public Task<string?> ImageIdAsync(string image, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<Routines.CommandCheckResult> RunCheckAsync(Containers.CheckContainerSpec spec, CancellationToken ct = default) => throw new InvalidOperationException("No supervisor.");
    public Task EnsureNetworksAsync(CancellationToken ct = default) => throw new InvalidOperationException("No supervisor.");
    public Task<string> EnsureServiceAsync(Containers.ServiceContainerSpec spec, CancellationToken ct = default) => throw new InvalidOperationException("No supervisor.");
    public Task StopServiceAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
}
