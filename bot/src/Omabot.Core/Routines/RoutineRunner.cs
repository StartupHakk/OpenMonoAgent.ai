// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Acp;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Runtime;
using Omabot.Team;

namespace Omabot.Routines;

public sealed record RoutineRunRequest(long RunId, BotRow Bot, RoutineSpec Routine, string Trigger, DateTimeOffset ScheduledFor, string CheckOutput, TurnLease Lease);

public sealed record RoutineRunResult(string Status, string? Reason, string Text);

/// <summary>Runs the model part of a routine. The scheduler has already run the checks and holds the bot's turn lease.</summary>
public interface IRoutineRunner
{
    Task<RoutineRunResult> RunAsync(RoutineRunRequest req, CancellationToken ct);
}

/// <summary>
/// Drives a routine turn directly over ACP: a fresh session by default, build
/// mode, the routine turn gate (changing tools blocked on edits and auto
/// bots), the harness-written envelope and the run time limit. Only a spent
/// step budget is continued; provider errors, an unreachable inference server
/// and denied tools end the run as an error (plan 7.4 continuation rule).
/// </summary>
public sealed class AcpRoutineRunner(TurnDriver driver, RosterConfig config, IClock clock) : IRoutineRunner
{
    public const string StepCapMarker = "Safety cap reached";
    public const string ContinueMessage = "[roster] Your last turn stopped at its step limit. Continue from where you stopped, then finish.";

    public async Task<RoutineRunResult> RunAsync(RoutineRunRequest req, CancellationToken ct)
    {
        var message = Envelope.Routine(req.Bot.Name, req.Routine.Name, req.RunId, RoutineRules.LocalShort(req.ScheduledFor, req.Routine.Timezone),
            req.Routine.Timezone, req.Routine.Instructions, req.CheckOutput);
        var limit = TimeSpan.FromMinutes(config.Routines.MaxRunMinutes);
        var started = clock.UtcNow;
        var report = await driver.RunAsync(new TurnRequest(req.Bot.Slug, message, "routine", req.Routine.Session, "build", TurnGate.RoutineBlocks(req.Bot.Spec), limit), ct).ConfigureAwait(false);
        if (report.End == TurnEnd.Done && report.Text.Contains(StepCapMarker, StringComparison.Ordinal) && report.SessionId is { } sid)
        {
            var left = limit - (clock.UtcNow - started);
            if (left > TimeSpan.FromSeconds(30))
                report = await driver.RunAsync(new TurnRequest(req.Bot.Slug, ContinueMessage, "routine", "id:" + sid, null, TurnGate.RoutineBlocks(req.Bot.Spec), left), ct).ConfigureAwait(false);
        }
        if (report.End == TurnEnd.Paused && report.SessionId is { } paused)
        {
            await driver.EndPausedAsync(req.Bot.Slug, paused, ct).ConfigureAwait(false);
            return new RoutineRunResult(RoutineStore.Status.NeedsInput, $"{report.Pause!.Kind}: {Redactor.Shared.Redact(report.Pause.Summary)}", report.Text);
        }
        return Classify(report);
    }

    public static RoutineRunResult Classify(TurnReport report)
    {
        if (report.TimedOut) return new(RoutineStore.Status.Error, "time_limit", report.Text);
        if (report.Problem is not null) return new(RoutineStore.Status.Error, "bot_unavailable", report.Text);
        return report.End switch
        {
            TurnEnd.Done => new(RoutineStore.Status.Ok, null, report.Text),
            TurnEnd.Paused => new(RoutineStore.Status.NeedsInput, report.Pause?.Kind.ToString(), report.Text),
            TurnEnd.Busy => new(RoutineStore.Status.Error, "session_busy", report.Text),
            TurnEnd.StreamCut => new(RoutineStore.Status.Error, "stream_cut", report.Text),
            _ => new(RoutineStore.Status.Error, ReasonFor(report.Error), report.Text),
        };
    }

    public static string ReasonFor(string? error)
    {
        var e = (error ?? "").ToLowerInvariant();
        if (e.Contains("inference_unreachable", StringComparison.Ordinal) || e.Contains("unreachable", StringComparison.Ordinal)) return "inference_unreachable";
        if (e.Contains("timeout", StringComparison.Ordinal) || e.Contains("timed out", StringComparison.Ordinal)) return "inference_timeout";
        if (e.Contains("denied", StringComparison.Ordinal)) return "tool_denied";
        return "turn_error";
    }
}
