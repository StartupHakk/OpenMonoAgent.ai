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
        ["run"] = (RunTurn, "run BOT \"message\" [--json]: one turn."),
        ["events"] = (Events, "events BOT [--lines N]: show the bot's recent turn events."),
        ["doctor"] = (DoctorCmd, "doctor [--json] [--quiet] [--online]: health check."),
        ["enable"] = (Enable, "enable BOT: re-enable a disabled bot."),
        ["disable"] = (Disable, "disable BOT: stop and disable a bot."),
        ["notices"] = (NoticesCmd, "notices [--seen ID]: unseen roster notices."),
        ["audit"] = (AuditCmd, "audit tail [N] | audit verify"),
        ["serve"] = (ServeAsync, "serve [--once]: run the gateway and health monitor (the roster service)."),
        ["gateway"] = (GatewayCmd, "gateway status | slots [--backend NAME] | bench [--backend NAME] [--levels 1,2,4] [--save]"),
        ["secrets"] = (SecretsCmd, "secrets set NAME [--bot BOT] [--file PATH] (value on stdin) | list [--bot BOT] | rm NAME [--bot BOT] | rotate-bot BOT"),
        ["usage"] = (UsageCmd, "usage [--bot BOT] [--days N] [--json]: model calls and tokens per bot and routine."),
        ["memory"] = (MemoryCmd, "memory show|journal [--lines N]|undo ID|forget \"fact\" [--topic T]|scan|tidy BOT"),
        ["render"] = (RenderCmd, "render BOT: write the bot's files without starting it (prints file paths)."),
    };

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
        => new(db, config, env.Paths, env.Clock, audit, notices, sup, env.Acp, env.Launch ?? LaunchOptions.Default(env.AppDir));

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
        var session = bots.GetSession(row.Id) ?? throw new ArgsException($"{row.Name} has no session yet. Run oma-bot up {row.Name}.");
        var acp = env.Acp(row.Port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(timeout is null ? row.Spec.MaxTurnMinutes : int.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture)));
        var log = Path.Combine(env.Paths.Logs, $"events-{row.Slug}.log");
        AcpTurnResult result;
        var contexts = new Gateway.TurnContexts(db, env.Clock, Gateway.Priority.Assignment);
        contexts.Set(row.Slug, Gateway.Priority.Interactive, null, TimeSpan.FromMinutes(row.Spec.MaxTurnMinutes + 1));
        try
        {
            result = await acp.SendAsync(session, message, e =>
            {
                File.AppendAllText(log, $"{env.Clock.UtcNow:O} {e.Name} {Redactor.Shared.Redact(e.Data)}\n");
                if (json) env.Out.WriteLine(JsonSerializer.Serialize(new { @event = e.Name, data = Redactor.Shared.Redact(e.Data) }));
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await acp.AbortAsync(session, CancellationToken.None).ConfigureAwait(false);
            env.Err.WriteLine($"{row.Name} did not finish in time; the turn was aborted.");
            return Exit.TimedOut;
        }
        finally
        {
            contexts.Clear(row.Slug);
        }
        bots.Touch(row.Slug);
        audit.Write("person", "bot.turn", row.Name, $"end {result.End} tools {result.ToolsUsed.Count}");
        AfterMemoryTurn(db, row, original, result.Text, result.End == TurnEnd.Done, $"end {result.End}, {result.ToolsUsed.Count} tool call(s)");
        if (!json && result.Text.Length > 0) env.Out.WriteLine(result.Text);
        switch (result.End)
        {
            case TurnEnd.Done:
                return Exit.Ok;
            case TurnEnd.Paused:
                env.Err.WriteLine($"{row.Name} is waiting for an answer ({result.Pause!.Kind}): {Redactor.Shared.Redact(result.Pause.Summary)}");
                return Exit.NeedsInput;
            case TurnEnd.Busy:
                env.Err.WriteLine($"{row.Name} is busy with another turn.");
                return Exit.TurnFailed;
            default:
                env.Err.WriteLine($"Turn failed: {result.Error ?? result.End.ToString()}");
                return Exit.TurnFailed;
        }
    }

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
            new DoctorOptions(env.AppDir, !online, env.Supervisor(), CliEnv.SharedHttp), ct).ConfigureAwait(false);
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
        var sub = a.Next("audit tail [N] | audit verify");
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
}
