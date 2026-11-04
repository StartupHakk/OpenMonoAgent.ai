// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Db;
using Omabot.Routines;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    /// <summary>The person at the CLI. The roster takes this from the OS account, never from an argument.</summary>
    private Approver PersonAtCli => Approver.Person(Environment.UserName.Length > 0 ? Environment.UserName : "local");

    private Task<int> RoutinesCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("routines list|runs|next|run-now|pause|resume [BOT] [ROUTINE]");
        var (db, audit, _, bots) = OpenDb();
        var store = new RoutineStore(db, env.Clock);
        store.Sync(bots.All());
        switch (sub)
        {
            case "list":
            {
                var name = a.NextOrNull();
                a.NoMore();
                var rows = name is null ? bots.All() : [bots.Get(name)];
                var any = false;
                foreach (var b in rows)
                {
                    foreach (var r in b.Spec.Routines)
                    {
                        any = true;
                        var st = store.Find(b.Slug, r.Name);
                        var state = !b.Enabled ? "bot disabled" : !r.Enabled ? "off" : st?.Paused == true ? "paused" : st?.Done == true ? "done" : "on";
                        var next = st?.NextFire is { } n ? RoutineRules.LocalShort(n, r.Timezone) : RoutineRules.NextThree(r, env.Clock.UtcNow, env.Clock.UtcNow).Select(t => RoutineRules.LocalShort(t, r.Timezone)).FirstOrDefault() ?? "-";
                        env.Out.WriteLine($"{b.Name,-16} {r.Name,-22} {r.Describe(),-34} {state,-8} next {next,-16} last {st?.LastStatus ?? "-"}");
                    }
                }
                if (!any) env.Out.WriteLine("No routines.");
                return Task.FromResult(Exit.Ok);
            }
            case "runs":
            {
                var lines = int.Parse(a.Option("--lines") ?? "20", CultureInfo.InvariantCulture);
                var name = a.NextOrNull();
                var routine = a.NextOrNull();
                a.NoMore();
                var slug = name is null ? null : bots.Get(name).Slug;
                var runs = store.Runs(slug, routine, lines);
                if (runs.Count == 0) env.Out.WriteLine("No runs yet.");
                foreach (var r in runs.Reverse())
                    env.Out.WriteLine($"{r.Id,6} {RoutineStore.Key(r.ScheduledFor)} {r.Bot,-14} {r.Routine,-20} {r.Status,-15} checks={r.Checks} model_calls={r.ModelCalls} tokens={r.Tokens}{(r.Reason is null ? "" : " reason=" + r.Reason)}");
                return Task.FromResult(Exit.Ok);
            }
            case "next" or "run-now" or "pause" or "resume":
            {
                var name = a.Next($"routines {sub} BOT ROUTINE");
                var routine = a.Next($"routines {sub} BOT ROUTINE");
                a.NoMore();
                var bot = bots.Get(name);
                var spec = bot.Spec.Routines.FirstOrDefault(r => Names.SameName(r.Name, routine)) ?? throw new ArgsException($"{bot.Name} has no routine named {routine}.");
                var st = store.Find(bot.Slug, spec.Name)!;
                switch (sub)
                {
                    case "next":
                        foreach (var t in RoutineRules.NextThree(spec, env.Clock.UtcNow, st.LastFire ?? st.Anchor)) env.Out.WriteLine(RoutineRules.Local(t, spec.Timezone));
                        break;
                    case "run-now":
                        store.SetRunNow(st.Id, env.Clock.UtcNow);
                        audit.Write(PersonAtCli.ToString(), "routine.run_now", $"{bot.Name}/{spec.Name}", "");
                        env.Out.WriteLine($"Queued {bot.Name}/{spec.Name} to run on the next roster service tick (oma-bot serve must be running).");
                        break;
                    case "pause":
                        store.SetPaused(st.Id, true);
                        audit.Write(PersonAtCli.ToString(), "routine.pause", $"{bot.Name}/{spec.Name}", "");
                        env.Out.WriteLine($"Paused {bot.Name}/{spec.Name}.");
                        break;
                    default:
                        store.SetPaused(st.Id, false);
                        audit.Write(PersonAtCli.ToString(), "routine.resume", $"{bot.Name}/{spec.Name}", "");
                        env.Out.WriteLine($"Resumed {bot.Name}/{spec.Name}.");
                        break;
                }
                return Task.FromResult(Exit.Ok);
            }
            default:
                throw new ArgsException("Use: oma-bot routines list|runs|next|run-now|pause|resume [BOT] [ROUTINE]");
        }
    }

    private Task<int> ApprovalsCmd(Args a, CancellationToken ct)
    {
        var answer = a.Option("--answer");
        var reason = a.Option("--reason");
        var hash = a.Option("--hash");
        var text = a.Option("--text");
        var all = a.Flag("--all");
        var config = LoadConfig();
        var (db, audit, _, _) = OpenDb();
        var store = new ApprovalStore(db, env.Clock, Redactor.Shared);
        store.ExpireDue();
        if (answer is not null)
        {
            var decision = a.Next("approvals --answer ID allow|deny");
            a.NoMore();
            if (decision is not ("allow" or "deny")) throw new ArgsException("Answer allow or deny.");
            var result = store.Decide(answer, PersonAtCli, decision == "allow", reason, hash, Policy(config, db), text);
            (result.Ok ? env.Out : env.Err).WriteLine(result.Message);
            if (!result.Ok) return Task.FromResult(Exit.Failed);
            audit.Write(PersonAtCli.ToString(), "approval." + decision, answer, result.Row?.Summary ?? "");
            if (result.Row?.Kind == ApprovalKinds.RoutineChange)
                env.Out.WriteLine(new RoutineChanges(db, config, env.Clock, audit, store).Apply(store.Get(answer)!));
            else if (result.Row?.Kind == ApprovalKinds.InstructionChange)
                env.Out.WriteLine(BotChangesFor(config, db, audit, store).Apply(store.Get(answer)!));
            else if (result.Row is { } row && (ApprovalKinds.IsPause(row.Kind) || row.Kind == ApprovalKinds.Ask))
                env.Out.WriteLine("The roster service delivers the answer to the bot on its next tick.");
            return Task.FromResult(Exit.Ok);
        }
        var sub = a.NextOrNull();
        if (sub == "show")
        {
            var id = a.Next("approvals show ID");
            a.NoMore();
            var row = store.Get(id) ?? throw new ArgsException($"No approval {id}.");
            env.Out.WriteLine($"{row.Id} {row.Kind} {row.Status}\nbot: {row.Bot}\nrequested by: {row.RequesterKind}:{row.Requester} (origin {row.Origin})\nsummary: {row.Summary}\naction hash: {row.ActionHash}\ncreated {row.CreatedAt:O}, expires {row.ExpiresAt:O}");
            if (row.Action is { Length: > 0 } action) env.Out.WriteLine("action: " + action);
            if (row.PersonOnly) env.Out.WriteLine("only a person may answer this card");
            if (row.BoundTo is not null) env.Out.WriteLine("bound to: " + row.BoundTo);
            if (row.Approver is not null) env.Out.WriteLine($"answered by {row.ApproverKind}:{row.Approver}: {row.Decision}{(row.Reason is null ? "" : " (" + row.Reason + ")")}");
            env.Out.WriteLine("payload: " + row.Payload);
            foreach (var e in store.Events(id)) env.Out.WriteLine($"  {e.At} {e.Actor} {e.Event} {e.Detail}");
            return Task.FromResult(Exit.Ok);
        }
        if (sub is not null) throw new ArgsException("Use: oma-bot approvals [--all] | approvals show ID | approvals --answer ID allow|deny [--reason TEXT]");
        a.NoMore();
        var list = all ? store.Recent() : store.Pending();
        if (list.Count == 0) env.Out.WriteLine(all ? "No approvals yet." : "Nothing waiting for an answer.");
        foreach (var r in list)
        {
            var left = r.ExpiresAt - env.Clock.UtcNow;
            var expires = r.Status == ApprovalStatus.Pending ? $"expires in {(left.TotalMinutes >= 120 ? $"{left.TotalHours:0} h" : $"{Math.Max(0, left.TotalMinutes):0} min")}" : r.Status;
            env.Out.WriteLine($"{r.Id}  {r.Kind,-16} {r.Bot,-14} {expires,-18} {r.Summary}");
        }
        return Task.FromResult(Exit.Ok);
    }

    /// <summary>The approver policy for this roster: the no-self-approval rules of plan 7.6.</summary>
    internal IApproverPolicy Policy(Config.RosterConfig config, RosterDb db) => ApproverPolicyFactory(config, db);

    internal Func<Config.RosterConfig, RosterDb, IApproverPolicy> ApproverPolicyFactory { get; init; } = (config, db) => new ApproverPolicy(db, env.Clock, config);

    internal Runtime.BotChanges BotChangesFor(Config.RosterConfig config, RosterDb db, AuditLog audit, ApprovalStore store)
        => new(db, config, env.Paths, env.Clock, audit, new Notices(db, env.Clock, Redactor.Shared), store)
        {
            Provision = spec => new Render.BotRenderer(env.Paths, config).EnsureFolders(spec),
        };
}
