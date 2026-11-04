// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Runtime;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    private Runtime.AssignmentRuntime OpenRuntime()
    {
        var config = LoadConfig();
        var (db, audit, notices, _) = OpenDb();
        var rt = new Runtime.AssignmentRuntime(db, config, env.Paths, env.Clock, audit, notices, new Runtime.TurnDriver(db, env.Clock, env.Paths, env.Acp));
        rt.Pauses = PauseHandlerFactory(rt);
        return rt;
    }

    private async Task<int> AssignmentsCmd(Args a, CancellationToken ct)
    {
        var all = a.Flag("--all");
        var lines = int.Parse(a.Option("--lines") ?? "30", CultureInfo.InvariantCulture);
        var sub = a.NextOrNull() ?? "list";
        var rt = OpenRuntime();
        switch (sub)
        {
            case "list":
            {
                a.NoMore();
                var rows = rt.Store.Recent(lines, liveOnly: !all);
                if (rows.Count == 0) env.Out.WriteLine(all ? "No assignments yet." : "No live assignments.");
                foreach (var r in rows.Reverse())
                    env.Out.WriteLine($"{r.Id}  {r.Status,-11} {r.Mode,-7} {(r.RequesterBot ?? r.RequesterKind),-12} -> {r.AssigneeBot,-14} depth {r.Depth} {Short(r.Brief)}");
                return Exit.Ok;
            }
            case "show":
            {
                var id = a.Next("assignments show ID");
                a.NoMore();
                var r = rt.Store.Get(id) ?? throw new ArgsException($"No assignment {id}.");
                env.Out.WriteLine($"{r.Id} ({r.Mode}, {r.Status}{(r.Outcome is null ? "" : ", " + r.Outcome)})");
                env.Out.WriteLine($"root {r.RootId}, parent {r.ParentId ?? "-"}, depth {r.Depth}, origin {r.Origin}, level {r.Level}");
                env.Out.WriteLine($"from {(r.RequesterBot ?? r.RequesterKind)} to {r.AssigneeBot}, executions {r.Executions}, restarts {r.Restarts}, tokens {r.TokensUsed}");
                env.Out.WriteLine("brief: " + r.Brief);
                if (r.OutcomeDetail is not null) env.Out.WriteLine("detail: " + r.OutcomeDetail);
                if (r.Evidence is not null) env.Out.WriteLine("evidence: " + r.Evidence);
                if (r.Result is not null) env.Out.WriteLine("result:\n" + r.Result);
                return Exit.Ok;
            }
            case "cancel":
            {
                var id = a.Next("assignments cancel ID");
                var reason = a.Option("--reason") ?? "cancelled by Spencer";
                a.NoMore();
                var res = await rt.CancelAsync(id, PersonAtCli.ToString(), reason, ct).ConfigureAwait(false);
                (res.Ok ? env.Out : env.Err).WriteLine(res.Message);
                return res.Ok ? Exit.Ok : Exit.Failed;
            }
            default:
                throw new ArgsException("Use: oma-bot assignments [list] [--all] | show ID | cancel ID [--reason TEXT]");
        }
    }

    /// <summary>`oma-bot audit tree ROOT_ID`: every event of the tree in order, with times, actors and short results (plan 7.5).</summary>
    private int AuditTree(string rootId)
    {
        var rt = OpenRuntime();
        var root = rt.Store.Get(rootId) ?? throw new ArgsException($"No assignment {rootId}.");
        if (!root.IsRoot) env.Out.WriteLine($"{rootId} is not a root; showing its tree {root.RootId}.");
        var rows = rt.Store.Tree(root.RootId).ToDictionary(r => r.Id);
        foreach (var e in rt.Store.TreeEvents(root.RootId))
        {
            var r = rows[e.AssignmentId];
            var indent = new string(' ', r.Depth * 2);
            var move = e.From is null ? $"-> {e.To}" : e.From == e.To ? e.To : $"{e.From} -> {e.To}";
            env.Out.WriteLine($"{e.At} {indent}{e.AssignmentId} {r.AssigneeBot,-12} {move,-24} {e.Actor}: {e.Reason}");
        }
        foreach (var r in rows.Values.Where(r => r.IsTerminal).OrderBy(r => r.CreatedAt))
            env.Out.WriteLine($"result {r.Id} ({r.AssigneeBot}, {r.Status}): {Short(r.Result ?? r.OutcomeDetail ?? "")}");
        return Exit.Ok;
    }

    private static string Short(string s)
    {
        var one = s.Replace('\n', ' ').Trim();
        return one.Length <= 80 ? one : one[..80] + "...";
    }
}
