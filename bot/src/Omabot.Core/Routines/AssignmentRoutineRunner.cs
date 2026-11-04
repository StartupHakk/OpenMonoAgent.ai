// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Config;
using Omabot.Gateway;
using Omabot.Runtime;

namespace Omabot.Routines;

/// <summary>
/// Routine runs as root assignments (plan 7.4 "Run", phase 5 task 6): origin
/// routine, mode work, the routine's instructions as the brief and the check
/// output as input, run at once on the lease the scheduler took. Work the
/// routine hands out carries origin routine down the tree, at ask level.
/// </summary>
public sealed class AssignmentRoutineRunner(AssignmentRuntime runtime, RosterConfig config, IClock clock) : IRoutineRunner
{
    public async Task<RoutineRunResult> RunAsync(RoutineRunRequest req, CancellationToken ct)
    {
        var message = Envelope.Routine(req.Bot.Name, req.Routine.Name, req.RunId, RoutineRules.LocalShort(req.ScheduledFor, req.Routine.Timezone),
            req.Routine.Timezone, req.Routine.Instructions, req.CheckOutput);
        var input = req.CheckOutput.Length > config.Handoffs.InputMaxBytes / 2 ? req.CheckOutput[..(config.Handoffs.InputMaxBytes / 2)] : req.CheckOutput;
        var e = runtime.Enqueue(new EnqueueRequest(
            To: req.Bot.Slug, Mode: "work", Brief: $"Routine {req.Routine.Name}: {(string.IsNullOrWhiteSpace(req.Routine.Instructions) ? "(no instructions)" : req.Routine.Instructions)}",
            Input: string.IsNullOrWhiteSpace(input) ? null : input, Rework: true, Origin: "routine", OriginRef: $"routine {req.Routine.Name} run {req.RunId}",
            RequesterKind: "routine", Session: req.Routine.Session, Reply: "poll", Priority: Priority.Routine, RoutineRunId: req.RunId,
            Actor: "routine:" + req.Routine.Name, ClaimWith: req.Lease, TurnMessage: message));
        if (!e.Ok || e.Id is null) return new RoutineRunResult(RoutineStore.Status.Error, "not_accepted: " + e.Reason, e.Message);
        new RoutineStore(runtime.Store.Db, clock).SetRunAssignment(req.RunId, e.Id);
        var (_, row) = await runtime.RunNowAsync(e.Id, req.Lease, "build", null, ct, TimeSpan.FromMinutes(config.Routines.MaxRunMinutes)).ConfigureAwait(false);
        return row.Status switch
        {
            AssignmentStatus.Completed => new RoutineRunResult(RoutineStore.Status.Ok, null, row.Result ?? ""),
            AssignmentStatus.Waiting => new RoutineRunResult(RoutineStore.Status.Ok, "handed out work", row.Result ?? ""),
            AssignmentStatus.Queued => new RoutineRunResult(RoutineStore.Status.Ok, "continues in a later turn", row.Result ?? ""),
            AssignmentStatus.NeedsInput => new RoutineRunResult(RoutineStore.Status.NeedsInput, "waiting for Spencer", row.Result ?? ""),
            AssignmentStatus.Cancelled => new RoutineRunResult(RoutineStore.Status.Error, "cancelled", row.Result ?? ""),
            _ => new RoutineRunResult(RoutineStore.Status.Error, row.Outcome switch
            {
                Outcomes.Aborted => "time_limit",
                Outcomes.Stalled => "stalled",
                Outcomes.Budget => "budget",
                _ => AcpRoutineRunner.ReasonFor(row.OutcomeDetail),
            }, row.Result ?? ""),
        };
    }
}
