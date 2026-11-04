// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Acp;
using Omabot.Api;
using Omabot.Common;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Render;
using Omabot.Routines;
using Omabot.Runtime;
using Omabot.Team;

namespace Omabot.Approvals;

/// <summary>What the broker decided for one pause before anyone saw it.</summary>
public sealed record BrokerVerdict(string Kind, string Reason, bool PersonOnly = false)
{
    public const string HardDeny = "hard_deny";
    public const string AutoAllow = "auto_allow";
    public const string Ask = "ask";
}

/// <summary>
/// The approval broker (plan 7.6). Every pause in a roster-started turn
/// comes here. The safety floor and hard deny rules answer deny at once,
/// team file auto rules answer allow at once, and everything else becomes a
/// card for Spencer, or for an approver bot Spencer named, while the pause
/// stays open in the bot. The broker alone answers pauses: it posts the
/// decision to the right session's turn endpoint and then reads the rest of
/// the turn. A pause answered by anything else (the bot itself through its
/// own ACP listener) is caught when the broker's answer is refused: the turn
/// is aborted, the bot disabled and Spencer alarmed (threat T3, SEC-27).
/// </summary>
public sealed partial class ApprovalBroker : IPauseHandler
{
    private readonly AssignmentRuntime _rt;
    private readonly RosterDb _db;
    private readonly IClock _clock;
    private readonly RosterPaths _paths;
    private readonly AuditLog _audit;
    private readonly Notices _notices;
    private readonly BotStore _bots;
    private readonly RoutineStore _routines;

    public ApprovalBroker(AssignmentRuntime runtime, IApproverPolicy? policy = null)
    {
        _rt = runtime;
        _db = runtime.Db;
        _clock = runtime.Clock;
        _paths = runtime.Paths;
        _audit = runtime.Audit;
        _notices = runtime.Notices;
        _bots = new BotStore(_db, _clock);
        _routines = new RoutineStore(_db, _clock);
        Policy = policy ?? new ApproverPolicy(_db, _clock, runtime.Config);
    }

    public IApproverPolicy Policy { get; }

    /// <summary>Stops a bot's container after the self-answer alarm. The service sets it; tests may leave it empty.</summary>
    public Func<string, CancellationToken, Task>? StopBot { get; set; }

    private ApprovalStore Approvals => _rt.Approvals;
    private TimeSpan PauseTtl => TimeSpan.FromMinutes(_rt.Config.Approvals.PauseTimeoutMinutes);

    // ---------------------------------------------------------------- policy

    public BrokerVerdict Evaluate(AssignmentRow a, BotRow bot, PauseCard card)
    {
        var commandLike = card.Kind is ApprovalKinds.HostExec or ApprovalKinds.Playbook || card.Tool is "Bash" or "HostExec";
        if (commandLike)
        {
            foreach (var text in new[] { card.Command, card.Action }.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var floor = SafetyFloor.Check(text);
                if (floor.Refused) return new BrokerVerdict(BrokerVerdict.HardDeny, "safety floor: the action " + floor.Reason);
            }
        }
        if (card.Tool is { } t && ToolCatalog.IsExternalPost(t)) return new BrokerVerdict(BrokerVerdict.HardDeny, $"{t} posts outside the box");
        if (HardDenyHost(card.Action + " " + card.Command) is { } host) return new BrokerVerdict(BrokerVerdict.HardDeny, $"the action mentions {host}");

        if (a.Mode is "consult" or "notify")
        {
            if (card.Kind is ApprovalKinds.ToggleMode or ApprovalKinds.Plan or ApprovalKinds.HostExec or ApprovalKinds.Playbook)
                return new BrokerVerdict(BrokerVerdict.HardDeny, $"a {a.Mode} answers only and changes nothing");
            if (card.Kind == ApprovalKinds.Permission && card.Tool is { } ct && (TurnGate.ChangingTools.Contains(ct) || ToolCatalog.IsOutward(ct) || ToolCatalog.WriteTools.Contains(ct)))
                return new BrokerVerdict(BrokerVerdict.HardDeny, $"a {a.Mode} answers only and changes nothing");
        }

        var hostExec = card.Kind == ApprovalKinds.HostExec || card.Tool == "HostExec";
        var routine = RoutineOf(a);
        if (hostExec)
        {
            if (!bot.Spec.HostExec) return new BrokerVerdict(BrokerVerdict.HardDeny, "host_exec is off for this bot in the team file");
            if (a.Origin == "routine" && routine?.HostExec != "ask")
                return new BrokerVerdict(BrokerVerdict.HardDeny, "a routine run uses host commands only when the routine says host_exec: ask");
        }

        // Always asks: root host commands and every host command, outside-steered work, and outward actions from routines.
        var outward = card.Tool is { } ot && ToolCatalog.IsOutward(ot);
        var personOnly = card.AsRoot || hostExec || a.Origin == "outside" || (a.Origin == "routine" && (outward || card.Kind == ApprovalKinds.Playbook));
        if (!personOnly && a.Level != "ask")
        {
            var rule = AutoRules.Match(bot.Spec.AutoAllow, card, _rt.Config.Approvals.AlwaysAsk, new BotRenderer(_paths, _rt.Config).WorkspaceOf(bot.Spec));
            if (rule is not null) return new BrokerVerdict(BrokerVerdict.AutoAllow, "team file rule: " + rule);
        }
        return new BrokerVerdict(BrokerVerdict.Ask, personOnly ? "always asks" : "", personOnly);
    }

    [GeneratedRegex(@"[a-z0-9.-]+", RegexOptions.IgnoreCase)]
    private static partial Regex HostToken();

    private string? HardDenyHost(string text)
    {
        foreach (Match m in HostToken().Matches(text))
        {
            var token = m.Value.Trim('.').ToLowerInvariant();
            foreach (var h in _rt.Config.Approvals.HardDenyHosts)
                if (token == h || token.EndsWith("." + h, StringComparison.Ordinal)) return h;
        }
        return null;
    }

    /// <summary>The routine behind a piece of work, through its tree root's routine run.</summary>
    public RoutineSpec? RoutineOf(AssignmentRow a)
    {
        var root = a.IsRoot ? a : _rt.Store.Get(a.RootId);
        if (root?.RoutineRunId is not { } runId || _routines.Run(runId) is not { } run) return null;
        return _bots.Find(run.Bot)?.Spec.Routines.FirstOrDefault(r => Names.SameName(r.Name, run.Routine));
    }

    // ---------------------------------------------------------------- the pause

    public async Task<PauseOutcome> HandleAsync(AssignmentRow a, TurnReport report, TurnLease lease, CancellationToken ct)
    {
        var pause = report.Pause!;
        var sid = report.SessionId!;
        var bot = _bots.Find(a.AssigneeBot);
        if (bot is null) return new PauseOutcome(null, "the bot no longer exists");
        var card = PauseDescriber.Describe(pause);
        var verdict = Evaluate(a, bot, card);
        var request = new ApprovalRequest(card.Kind, bot.Slug, $"{bot.Name} {card.Summary}", card.Payload, "bot", bot.Slug, a.Origin, sid, pause.Id, a.Id, a.RootId,
            Ttl: PauseTtl, PersonOnly: verdict.PersonOnly, Action: card.Action);

        if (Approvals.ForPause(bot.Slug, sid, pause.Id) is { } existing)
        {
            // The same pause seen twice (a resumed turn re-raised it): never a second card or a second answer.
            Approvals.Audit(existing.Id, "roster", "pause_seen_again", "the bot raised the same pause again");
            return new PauseOutcome(null, $"waiting on {existing.Id}");
        }

        switch (verdict.Kind)
        {
            case BrokerVerdict.HardDeny:
                {
                    var row = Approvals.Create(request, ApprovalStatus.HardDenied, "roster", verdict.Reason);
                    _audit.Write("roster", "approval.hard_denied", bot.Name, $"{row.Id} {card.Kind}: {verdict.Reason}");
                    _notices.Add("warn", bot.Name, $"Denied for {bot.Name} without a card: {card.Summary} ({verdict.Reason}).", $"hard_deny:{bot.Slug}:{card.Tool ?? card.Kind}:{_clock.UtcNow:yyyyMMdd}");
                    return await AnswerNowAsync(a, row, pause, sid, false, ct).ConfigureAwait(false);
                }
            case BrokerVerdict.AutoAllow:
                {
                    var row = Approvals.Create(request, ApprovalStatus.AutoAllowed, "roster", verdict.Reason);
                    _audit.Write("roster", "approval.auto_allowed", bot.Name, $"{row.Id} {card.Kind}: {verdict.Reason}");
                    return await AnswerNowAsync(a, row, pause, sid, true, ct).ConfigureAwait(false);
                }
            default:
                {
                    var bound = verdict.PersonOnly ? null : PickApprover(a, bot, card);
                    var row = Approvals.Create(request with { BoundTo = bound is null ? null : "bot:" + bound.Slug });
                    _audit.Write("roster", "approval.requested", bot.Name, $"{row.Id} {card.Kind}{(bound is null ? "" : " bound to " + bound.Name)}");
                    _notices.Add("approval", bot.Name,
                        $"Approval needed ({row.Id}): {bot.Name} {card.Summary}. Answer with oma-bot approvals answer {row.Id} allow|deny within {_rt.Config.Approvals.PauseTimeoutMinutes} minutes.",
                        "approval:" + row.Id);
                    if (bound is not null) AskApproverBot(row, bound, bot);
                    return new PauseOutcome(null, $"waiting for an answer on {row.Id} ({card.Kind})");
                }
        }
    }

    /// <summary>The first listed approver bot the policy would accept for this card, or null (Spencer answers).</summary>
    private BotRow? PickApprover(AssignmentRow a, BotRow requester, PauseCard card)
    {
        if (Policy is not ApproverPolicy policy) return null;
        var probe = new ApprovalRow("", card.Kind, ApprovalStatus.Pending, null, requester.Slug, null, null, a.Id, a.RootId, a.Origin, "", "{}", "", "bot", requester.Slug,
            null, null, null, null, _clock.UtcNow, _clock.UtcNow, null, null);
        foreach (var name in ApproverList.For(_db, _rt.Config, card.Kind))
        {
            var me = _bots.Find(name);
            if (me is null || !me.Enabled || me.Spec.Role is "lead" or "health" || Names.SameName(me.Slug, requester.Slug)) continue;
            if (!RosterToolHost.BotMayUse(me.Spec, "approval_answer")) continue;
            if (policy.ControlRefusal(probe, me) is null) return me;
        }
        return null;
    }

    private void AskApproverBot(ApprovalRow row, BotRow approver, BotRow requester)
    {
        var action = row.Action ?? "";
        if (action.Length > 1500) action = action[..1500] + " (cut)";
        var brief = $"The roster bound approval card {row.Id} to you. {requester.Name} is the requester: {row.Summary}. "
                    + $"Read the action below, then answer with the roster approval_answer tool (id {row.Id}, decision allow or deny, and a short reason). "
                    + "Deny anything you are not sure is safe. You may answer only cards bound to you.";
        var r = _rt.Enqueue(new EnqueueRequest(To: approver.Slug, Mode: "notify", Brief: brief.Length > 3000 ? brief[..3000] : brief, Input: action,
            Origin: "roster", OriginRef: row.Id, RequesterKind: "roster", Actor: "roster", Rework: true));
        Approvals.Audit(row.Id, "roster", "bound", $"bound to bot:{approver.Slug}" + (r.Ok ? $" ({r.Id})" : $"; could not reach it: {r.Message}"));
    }

    private async Task<PauseOutcome> AnswerNowAsync(AssignmentRow a, ApprovalRow row, AcpPause pause, string sid, bool allow, CancellationToken ct)
    {
        var report = await AnswerAsync(a, row, pause, sid, allow, null, ct).ConfigureAwait(false);
        return report is null ? new PauseOutcome(null, "the pause was answered outside the roster") : new PauseOutcome(report, null);
    }

    /// <summary>Posts the answer and reads on. Null when the bot refused it because something else already answered the pause.</summary>
    private async Task<TurnReport?> AnswerAsync(AssignmentRow a, ApprovalRow row, AcpPause pause, string sid, bool allow, string? value, CancellationToken ct)
    {
        if (allow && row.PayloadJson()["tool"] is JsonValue tv && tv.TryGetValue<string>(out var tool) && ToolCatalog.IsOutward(tool))
            _rt.Store.RecordOutward(a.AssigneeBot, a.Id, sid, a.LeaseOwner ?? _rt.Turns.OwnerOf(a.AssigneeBot) ?? "", tool, $"{tool}: {row.Action ?? row.Summary} (approved {row.Id})", "approved");
        if (allow && row.Kind == ApprovalKinds.HostExec)
            _rt.Store.RecordOutward(a.AssigneeBot, a.Id, sid, a.LeaseOwner ?? _rt.Turns.OwnerOf(a.AssigneeBot) ?? "", "HostExec", $"HostExec: {row.Action} (approved {row.Id})", "approved");
        var report = await _rt.AnswerPauseAsync(a, pause, sid, allow, value, ct).ConfigureAwait(false);
        if (report.End == TurnEnd.ResumeError)
        {
            await SelfAnsweredAsync(a, row, sid, ct).ConfigureAwait(false);
            return null;
        }
        Approvals.MarkDelivered(row.Id, allow ? "allowed and delivered" : "denied and delivered");
        return report;
    }

    /// <summary>
    /// Threat T3 and SEC-27: the bot rejected the broker's answer because the
    /// pause was already resolved, so something other than the roster answered
    /// it. Abort the turn, disable the bot, alarm Spencer.
    /// </summary>
    private async Task SelfAnsweredAsync(AssignmentRow a, ApprovalRow row, string sid, CancellationToken ct)
    {
        var bot = _bots.Find(a.AssigneeBot);
        var name = bot?.Name ?? a.AssigneeBot;
        Approvals.Audit(row.Id, "roster", "self_answer_detected", "the pause was answered by something other than the roster");
        _audit.Write("roster", "alarm.self_answer", name, $"{row.Id} pause {row.PauseId} in session {sid} was answered outside the roster");
        await _rt.Driver.EndPausedAsync(a.AssigneeBot, sid, ct).ConfigureAwait(false);
        _bots.Disable(a.AssigneeBot, "a pause was answered outside the roster (" + row.Id + ")");
        _audit.Write("roster", "bot.disabled", name, "self-answer alarm " + row.Id);
        _notices.Add("alarm", name,
            $"ALARM: {name}'s pause {row.Id} was answered by something other than the roster. The turn was aborted and {name} is disabled. Check its events log, then run oma-bot enable {name}.",
            "self_answer:" + a.AssigneeBot);
        _rt.Settle(_rt.Store.Get(a.Id) ?? a, AssignmentStatus.Failed, Outcomes.Aborted, "a pause was answered outside the roster; the bot is disabled", null);
        Approvals.MarkDelivered(row.Id, "refused by the bot: already answered elsewhere");
        if (StopBot is not null)
        {
            try { await StopBot(a.AssigneeBot, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _audit.Write("roster", "bot.stop_failed", name, Redactor.Shared.Redact(ex.Message)); }
        }
    }

    // ---------------------------------------------------------------- service tick

    /// <summary>
    /// Delivers decided pause cards to their waiting turns, turns expired
    /// pauses into a follow-up question (pause_expired), cancels work that
    /// waited past the cancel time, and reminds about cards waiting long.
    /// </summary>
    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        Approvals.ExpireDue();
        foreach (var row in Approvals.ExpiredUndelivered())
            lines.AddRange(await ExpiredAsync(row, ct).ConfigureAwait(false));
        foreach (var row in Approvals.Undelivered().Where(r => ApprovalKinds.IsPause(r.Kind) && r.AssignmentId is not null))
            if (Deliver(row, ct) is { } line) lines.Add(line);
        var remindBefore = _clock.UtcNow - TimeSpan.FromHours(_rt.Config.Approvals.RemindAfterHours);
        foreach (var row in Approvals.PendingForReminder(remindBefore))
        {
            _notices.Add("warn", row.Bot, $"Still waiting since {row.CreatedAt:yyyy-MM-dd HH:mm} UTC on {row.Id}: {row.Summary}. It is cancelled at {row.ExpiresAt:yyyy-MM-dd HH:mm} UTC.", "remind:" + row.Id);
            Approvals.MarkReminded(row.Id);
            lines.Add($"{row.Id}: reminder");
        }
        return lines;
    }

    private string? Deliver(ApprovalRow row, CancellationToken ct)
    {
        var a = _rt.Store.Get(row.AssignmentId!);
        if (a is null || a.Status != AssignmentStatus.NeedsInput || row.SessionId is null)
        {
            if (a is null || a.IsTerminal || a.Status != AssignmentStatus.NeedsInput) Approvals.MarkDelivered(row.Id, "nothing was waiting for it any more");
            return null;
        }
        var allow = row.IsAllowed;
        var value = row.Kind == ApprovalKinds.UserInput ? row.PayloadJson()["answer"]?.GetValue<string>() ?? (allow ? "yes" : "no") : null;
        var started = _rt.ContinueParked(a.Id, $"answer {row.Id} delivered", async (current, lease, token) =>
            await AnswerAsync(current, row, PauseDescriber.Rebuild(row), row.SessionId, allow, value, token).ConfigureAwait(false), ct);
        return started ? $"{row.Id}: {(allow ? "allow" : "deny")} delivered to {a.Id}" : null;
    }

    private async Task<IReadOnlyList<string>> ExpiredAsync(ApprovalRow row, CancellationToken ct)
    {
        var lines = new List<string>();
        var a = row.AssignmentId is null ? null : _rt.Store.Get(row.AssignmentId);
        if (a is null || a.Status != AssignmentStatus.NeedsInput)
        {
            Approvals.MarkDelivered(row.Id, "expired; nothing was waiting");
            return lines;
        }
        if (ApprovalKinds.IsPause(row.Kind))
        {
            if (row.SessionId is { } sid) await _rt.Driver.EndPausedAsync(a.AssigneeBot, sid, ct).ConfigureAwait(false);
            var question = $"Your request ({row.Kind}) expired with no answer after {_rt.Config.Approvals.PauseTimeoutMinutes} minutes: {row.Summary}. "
                           + "Answer to let the bot try again, or tell it what to do instead.";
            var follow = Approvals.Create(new ApprovalRequest(ApprovalKinds.Ask, a.AssigneeBot, $"{row.Summary} (pause_expired)",
                new JsonObject { ["question"] = question, ["pause_expired"] = row.Id, ["action"] = row.Action }, "bot", a.AssigneeBot, a.Origin,
                AssignmentId: a.Id, RootId: a.RootId, Ttl: TimeSpan.FromHours(_rt.Config.Approvals.CancelAfterHours), PersonOnly: true));
            _rt.Store.Update(a.Id, "roster", "pause_expired", new Dictionary<string, object?>(), new { card = row.Id, follow_up = follow.Id });
            _audit.Write("roster", "approval.pause_expired", a.AssigneeBot, $"{row.Id} expired; follow-up {follow.Id}");
            _notices.Add("approval", a.AssigneeBot, $"{row.Id} expired with no answer. {a.AssigneeBot} waits on {follow.Id}: oma-bot approvals answer {follow.Id} allow --text \"what to do\".", "approval:" + follow.Id);
            Approvals.MarkDelivered(row.Id, "expired; follow-up " + follow.Id);
            lines.Add($"{row.Id}: pause expired, follow-up {follow.Id}");
            return lines;
        }
        if (row.Kind == ApprovalKinds.Ask && !Approvals.Pending().Any(p => p.AssignmentId == a.Id))
        {
            _rt.Settle(a, AssignmentStatus.Cancelled, Outcomes.Expired, $"no answer within {_rt.Config.Approvals.CancelAfterHours} hours ({row.Id})", a.Result);
            lines.Add($"{a.Id}: cancelled after {_rt.Config.Approvals.CancelAfterHours} hours without an answer");
        }
        Approvals.MarkDelivered(row.Id, "expired");
        return lines;
    }

    // ---------------------------------------------------------------- approver bots

    /// <summary>approval_answer: an approver bot answers a card the broker bound to it. The caller identity comes from the socket.</summary>
    public void Register(RosterToolHost host)
    {
        host.Add(new RosterTool("approval_answer", "Answer an approval card the roster bound to you (allow or deny, with a reason). You never answer your own requests.",
            RosterTool.Schema(("id", "string", "The card id (apv_...)", true), ("decision", "string", "allow or deny", true), ("reason", "string", "Why, in one line", false)),
            (c, a, _) =>
            {
                var id = RosterToolHost.Str(a, "id") ?? "";
                var decision = RosterToolHost.Str(a, "decision");
                if (decision is not ("allow" or "deny")) return Task.FromResult(ToolResult.Fail("decision must be allow or deny.", 400));
                var r = Approvals.Decide(id, Approver.Bot(c.Bot), decision == "allow", RosterToolHost.Str(a, "reason"), null, Policy);
                _audit.Write("bot:" + c.Bot, r.Ok ? "approval.answered" : "approval.refused", id, r.Message);
                return Task.FromResult(r.Ok ? new ToolResult(true, $"Recorded {decision} for {id}. The roster delivers it.") : ToolResult.Fail(r.Message, 403));
            }));
    }
}
