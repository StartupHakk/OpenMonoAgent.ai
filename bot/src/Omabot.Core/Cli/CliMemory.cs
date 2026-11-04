// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;
using Omabot.Api;
using Omabot.Common;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Memory;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    private Task<int> MemoryCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("memory show|journal|undo|forget|scan|tidy BOT");
        var name = a.Next("memory " + sub + " BOT");
        var (db, audit, _, bots) = OpenDb();
        var row = bots.Get(name);
        var store = MemoryStores.For(db, env.Clock, env.Paths, row.Spec);
        if (store is null)
        {
            env.Err.WriteLine($"Memory is off for {row.Name}.");
            return Task.FromResult(Exit.Invalid);
        }
        switch (sub)
        {
            case "show":
            {
                a.NoMore();
                var loaded = store.Load();
                env.Out.WriteLine(loaded.Block);
                env.Out.WriteLine($"\n(loaded {loaded.LinesLoaded} line(s){(loaded.Cut ? ", cut at the budget" : "")}; {loaded.ExpiredSkipped} expired entr{(loaded.ExpiredSkipped == 1 ? "y" : "ies")} not loaded)");
                env.Out.WriteLine("Topics:\n" + store.TopicIndex());
                return Task.FromResult(Exit.Ok);
            }
            case "journal":
            {
                var n = int.Parse(a.Option("--lines") ?? "20", CultureInfo.InvariantCulture);
                a.NoMore();
                foreach (var j in store.JournalRows(n))
                    env.Out.WriteLine($"{j.Id,6}  {j.At}  {j.Actor,-12} {j.File,-22} {j.BeforeHash}->{j.AfterHash}  {j.Reason}{(j.Undoable ? "" : " (no undo)")}");
                return Task.FromResult(Exit.Ok);
            }
            case "undo":
            {
                var id = long.Parse(a.Next("memory undo BOT JOURNAL_ID"), CultureInfo.InvariantCulture);
                a.NoMore();
                var r = store.Undo(id, "person");
                (r.Ok ? env.Out : env.Err).WriteLine(r.Message);
                if (r.Ok) audit.Write("person", "memory.undo", row.Name, $"journal {id}");
                return Task.FromResult(r.Ok ? Exit.Ok : Exit.Failed);
            }
            case "forget":
            {
                var topic = a.Option("--topic");
                var fact = a.Next("memory forget BOT \"fact\"");
                a.NoMore();
                var rel = topic is null ? MemoryStore.MemoryFile : MemoryStore.TopicFile(topic) ?? throw new ArgsException("Bad topic name.");
                var r = store.Forget(fact, topic, store.Read(rel).Rev, "person");
                env.Out.WriteLine(r.Message);
                return Task.FromResult(r.Ok || r.Status == MemoryStatus.NotFound ? Exit.Ok : Exit.Failed);
            }
            case "scan":
            {
                a.NoMore();
                var fixedFiles = store.ScanDirectWrites();
                env.Out.WriteLine(fixedFiles.Count == 0 ? "No secrets found in memory files." : "Redacted: " + string.Join(", ", fixedFiles));
                return Task.FromResult(Exit.Ok);
            }
            case "tidy":
            {
                a.NoMore();
                var up = new MemoryUpkeep(store, db, env.Clock, null, MemoryStores.TurnsFile(env.Paths, row.Slug));
                var r = up.TidyAsync(ct).GetAwaiter().GetResult();
                env.Out.WriteLine($"tidy: {r.Note} (contradiction check runs in the roster service, which has the model)");
                return Task.FromResult(Exit.Ok);
            }
            default:
                throw new ArgsException("memory show|journal|undo|forget|scan|tidy BOT");
        }
    }

    /// <summary>
    /// Before a turn: refresh OPENMONO.md with the current memory block and
    /// put recalled notes in front of Spencer's message (plan 7.3).
    /// </summary>
    internal string PrepareMemoryTurn(Supervision.BotLauncher launcher, BotRow row, string message, bool personTurn)
    {
        launcher.Renderer.WriteOpenMono(row.Spec, launcher.ContextFor(row));
        var store = MemoryStores.For(launcher.Db, env.Clock, env.Paths, row.Spec);
        if (store is null || !personTurn || !row.Spec.Recall) return message;
        return Recall.Apply(store, message);
    }

    internal void AfterMemoryTurn(RosterDb db, BotRow row, string message, string reply, bool ok, string endLabel)
    {
        var store = MemoryStores.For(db, env.Clock, env.Paths, row.Spec);
        if (store is null) return;
        store.AppendDailyLog($"turn from Spencer: {endLabel}");
        if (row.Spec.MemoryCapture)
            new MemoryUpkeep(store, db, env.Clock, null, MemoryStores.TurnsFile(env.Paths, row.Slug)).RecordPersonTurn(message, reply, ok);
        store.ScanDirectWrites();
    }
}

/// <summary>Upkeep calls through the in-process gateway at background priority.</summary>
public sealed class GatewayUpkeepModel(PassThroughGateway gateway, Func<string, string?> modelOf) : IUpkeepModel
{
    public Task<string?> CompleteAsync(string bot, string system, string user, CancellationToken ct)
    {
        var model = modelOf(bot);
        return model is null ? Task.FromResult<string?>(null) : gateway.InternalCompleteAsync(bot, model, Priority.Background, "memory-upkeep", system, user, 800, ct);
    }
}

/// <summary>Roster side services the serve loop runs each tick: the shim sockets and memory upkeep.</summary>
public sealed class RosterServices
{
    private readonly RosterDb _db;
    private readonly IClock _clock;
    private readonly RosterPaths _paths;
    private readonly BotStore _bots;
    private readonly IUpkeepModel? _model;

    public RosterServices(RosterDb db, IClock clock, RosterPaths paths, IUpkeepModel? model, Action<RosterToolHost>? register = null)
    {
        _db = db;
        _clock = clock;
        _paths = paths;
        _bots = new BotStore(db, clock);
        _model = model;
        Tokens = new ShimTokens(db, clock);
        Tools = new RosterToolHost((bot, tool) => RosterToolHost.BotMayUse(_bots.Find(bot)?.Spec, tool));
        register?.Invoke(Tools);
        MemoryTools.Register(Tools, bot => _bots.Find(bot) is { } r ? MemoryStores.For(db, clock, paths, r.Spec) : null);
        Tools.Add(new RosterTool("list_bots", "Names, titles and what each bot accepts.", RosterTool.Schema(), (_, _, _) =>
        {
            var arr = new JsonArray([.. _bots.All().Where(b => b.Enabled).Select(b => (JsonNode)new JsonObject
            {
                ["name"] = b.Name, ["title"] = b.Spec.Title, ["state"] = b.State, ["role"] = b.Spec.Role,
                ["version"] = Runtime.BotChanges.Version(b.Spec),
                ["accepts_from"] = new JsonArray([.. b.Spec.AcceptsFrom.Select(x => (JsonNode)x)]),
            })]);
            return Task.FromResult(new ToolResult(true, string.Join('\n', _bots.All().Where(b => b.Enabled).Select(b =>
                b.Name + (b.Spec.Title is null ? "" : " (" + b.Spec.Title + ")") + " version " + Runtime.BotChanges.Version(b.Spec))), arr));
        }));
        Shim = new ShimServer(Tools, Tokens);
    }

    public RosterToolHost Tools { get; }
    public ShimTokens Tokens { get; }
    public ShimServer Shim { get; }

    public bool Busy(string bot)
    {
        var exp = _db.Scalar("SELECT expires_at FROM turn_context WHERE bot=$b;", ("$b", bot)) as string;
        return exp is not null && DateTimeOffset.Parse(exp, CultureInfo.InvariantCulture) > _clock.UtcNow;
    }

    /// <summary>One tick: sockets for running bots, direct write scans, then upkeep for idle bots.</summary>
    public async Task<IReadOnlyList<string>> TickAsync(bool listen, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var row in _bots.All().Where(b => b.Enabled))
        {
            if (listen) await Shim.EnsureListeningAsync(row.Slug, _paths.Bot(row.Slug).ShimSocket, ct).ConfigureAwait(false);
            var store = MemoryStores.For(_db, _clock, _paths, row.Spec);
            if (store is null) continue;
            var flag = Path.Combine(_paths.Bot(row.Slug).Data, "roster-flags", "rescan-memory");
            if (File.Exists(flag)) File.Delete(flag);
            foreach (var f in store.ScanDirectWrites()) lines.Add($"{row.Name}: redacted a secret written directly to {f}.");
            if (Busy(row.Slug)) continue;
            var up = new MemoryUpkeep(store, _db, _clock, _model, MemoryStores.TurnsFile(_paths, row.Slug));
            if (row.Spec.MemoryCapture && up.CaptureDue())
            {
                var r = await up.CaptureAsync(ct).ConfigureAwait(false);
                if (r.Changed > 0) lines.Add($"{row.Name}: capture {r.Note}.");
                var o = await up.OrganizeAsync(ct).ConfigureAwait(false);
                if (o.Changed > 0) lines.Add($"{row.Name}: organize moved {o.Changed} line(s).");
            }
            if (row.Spec.MemoryTidy && up.TidyDue())
            {
                var t = await up.TidyAsync(ct).ConfigureAwait(false);
                await up.OrganizeAsync(ct).ConfigureAwait(false);
                if (t.Changed > 0) lines.Add($"{row.Name}: tidy {t.Note}.");
            }
        }
        return lines;
    }
}
