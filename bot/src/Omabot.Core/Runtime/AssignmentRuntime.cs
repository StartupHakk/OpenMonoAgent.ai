// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omabot.Acp;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Team;

namespace Omabot.Runtime;

public static class EnqueueReasons
{
    public const string Ok = "ok";
    public const string Joined = "joined";
    public const string Ancestor = "ancestor";
    public const string Depth = "depth";
    public const string Budget = "budget";
    public const string Fanout = "fanout";
    public const string TreeFull = "tree_full";
    public const string Refused = "refused";
    public const string NotFound = "not_found";
    public const string Disabled = "disabled";
    public const string ParentNotRunning = "parent_not_running";
    public const string Level = "level";
    public const string Invalid = "invalid";
}

/// <summary>Everything about one piece of handed-out work. The roster fills the requester fields from its own records.</summary>
public sealed record EnqueueRequest(
    string To,
    string Mode,
    string Brief,
    string? DoneWhen = null,
    string? Input = null,
    bool Rework = false,
    string? ParentId = null,
    string Origin = "person",
    string? OriginRef = null,
    string RequesterKind = "person",
    string? RequesterBot = null,
    string? RequesterSession = null,
    string? RequesterTurn = null,
    string? Session = null,
    string? Reply = null,
    Priority? Priority = null,
    long? RoutineRunId = null,
    string? RequestedLevel = null,
    string Actor = "roster",
    TurnLease? ClaimWith = null,
    string? TurnMessage = null,
    int? DeadlineMinutes = null);

public sealed record EnqueueResult(bool Ok, string? Id, string Reason, string Message)
{
    public static EnqueueResult Refuse(string reason, string message) => new(false, null, reason, message);
}

public sealed record CancelResult(bool Ok, string Message, int Cancelled);

/// <summary>How a paused turn goes on: a continued turn after an answer, or parked for a person.</summary>
public sealed record PauseOutcome(TurnReport? Continued, string? ParkedReason);

public interface IPauseHandler
{
    Task<PauseOutcome> HandleAsync(AssignmentRow a, TurnReport report, TurnLease lease, CancellationToken ct);
}

/// <summary>
/// The handoff runtime (plan 7.5): one record, one lifecycle and one wake
/// lane for every piece of work handed from one bot to another, whatever
/// started it. Enqueue checks return typed reasons and never throw into a
/// turn. Turns are admitted only when the bot is free and a gateway slot is
/// open. A result is reported once, and a waiting parent wakes once when
/// all its children have settled.
/// </summary>
public sealed partial class AssignmentRuntime
{
    private readonly RosterDb _db;
    private readonly RosterConfig _config;
    private readonly RosterPaths _paths;
    private readonly IClock _clock;
    private readonly AuditLog _audit;
    private readonly Notices _notices;
    private readonly TurnDriver _driver;
    private readonly Func<string, bool> _slotFree;
    private readonly BotStore _bots;
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);

    public AssignmentRuntime(RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit, Notices notices, TurnDriver driver, Func<string, bool>? slotFree = null)
    {
        _db = db;
        _config = config;
        _paths = paths;
        _clock = clock;
        _audit = audit;
        _notices = notices;
        _driver = driver;
        _slotFree = slotFree ?? (_ => true);
        _bots = new BotStore(db, clock);
        Store = new AssignmentStore(db, clock, Redactor.Shared);
        Turns = new TurnContexts(db, clock, Priority.Assignment);
        Approvals = new ApprovalStore(db, clock, Redactor.Shared);
        Pauses = new ParkingPauseHandler(this);
    }

    public AssignmentStore Store { get; }
    public TurnContexts Turns { get; }
    public ApprovalStore Approvals { get; }
    public TurnDriver Driver => _driver;
    public RosterConfig Config => _config;
    public IClock Clock => _clock;
    public RosterDb Db => _db;
    public RosterPaths Paths => _paths;
    public AuditLog Audit => _audit;
    public Notices Notices => _notices;

    /// <summary>What happens to a paused turn. Phase 6 sets the approval broker here.</summary>
    public IPauseHandler Pauses { get; set; }

    public Task WhenIdleAsync() => Task.WhenAll(_inflight.Values.ToArray());

    public static readonly string[] Profiles = ["ask", "edits", "auto"];
    public static int Rank(string level) => Math.Max(0, Array.IndexOf(Profiles, level));
    public static string MinLevel(string a, string b) => Rank(a) <= Rank(b) ? a : b;

    [GeneratedRegex(@"[\p{P}\p{S}]+")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Dedupe normalization: case, punctuation and spacing are set aside; nothing else.</summary>
    public static string Normalize(string s) => Spaces().Replace(Punctuation().Replace(s.ToLowerInvariant(), " "), " ").Trim();

    public static string DedupeKey(string brief, string assignee, string? parent, string mode)
        => SecureFile.Sha256Hex(string.Join("\u001f", Normalize(brief), assignee, parent ?? "", mode));

    // ---------------------------------------------------------------- enqueue

    /// <summary>Hands out work. Never throws for bad input: every refusal is a typed result (invariant I10).</summary>
    public EnqueueResult Enqueue(EnqueueRequest r)
    {
        try
        {
            return EnqueueCore(r);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return EnqueueResult.Refuse(EnqueueReasons.Invalid, "The roster could not accept this: " + Redactor.Shared.Redact(ex.Message));
        }
    }

    private EnqueueResult EnqueueCore(EnqueueRequest r)
    {
        var h = _config.Handoffs;
        var now = _clock.UtcNow;
        if (r.Mode is not ("work" or "consult" or "notify")) return EnqueueResult.Refuse(EnqueueReasons.Invalid, "mode must be work, consult or notify.");
        if (string.IsNullOrWhiteSpace(r.Brief)) return EnqueueResult.Refuse(EnqueueReasons.Invalid, "The brief is empty.");
        if (r.Brief.Length > h.BriefMaxChars) return EnqueueResult.Refuse(EnqueueReasons.Invalid, $"The brief is over {h.BriefMaxChars} characters.");
        if (r.Input is { } inp && System.Text.Encoding.UTF8.GetByteCount(inp) > h.InputMaxBytes) return EnqueueResult.Refuse(EnqueueReasons.Invalid, $"The input is over {h.InputMaxBytes} bytes.");
        if (r.DoneWhen is { Length: > 2000 }) return EnqueueResult.Refuse(EnqueueReasons.Invalid, "done_when is over 2,000 characters.");
        if (r.Mode == "notify" && System.Text.Encoding.UTF8.GetByteCount(r.Brief + (r.Input ?? "")) > h.NotifyMaxBytes)
            return EnqueueResult.Refuse(EnqueueReasons.Invalid, $"A notice is at most {h.NotifyMaxBytes} bytes.");

        AssignmentRow? parent = null;
        if (r.ParentId is not null)
        {
            parent = Store.Get(r.ParentId);
            if (parent is null) return EnqueueResult.Refuse(EnqueueReasons.NotFound, $"No assignment {r.ParentId}.");
            if (parent.Status != AssignmentStatus.Running) return EnqueueResult.Refuse(EnqueueReasons.ParentNotRunning, $"{parent.Id} is {parent.Status}; only a running turn hands out work.");
            if (r.RequesterBot is not null && parent.AssigneeBot != r.RequesterBot)
                return EnqueueResult.Refuse(EnqueueReasons.Invalid, "The parent assignment is not the requester's own turn.");
        }
        var requester = r.RequesterBot is null ? null : _bots.Find(r.RequesterBot);
        var route = Routing.Resolve(r.To, r.Brief, requester, _db, _bots);
        if (route.Bot is null) return EnqueueResult.Refuse(EnqueueReasons.NotFound, route.Problem ?? "No such bot.");
        var assignee = route.Bot;
        if (!assignee.Enabled) return EnqueueResult.Refuse(EnqueueReasons.Disabled, $"{assignee.Name} is disabled.");

        // No work back up the chain: the assignee may not be the requester or any ancestor.
        for (var node = parent; node is not null; node = node.ParentId is null ? null : Store.Get(node.ParentId))
            if (node.AssigneeBot == assignee.Slug) return EnqueueResult.Refuse(EnqueueReasons.Ancestor, $"{assignee.Name} is already working on this tree ({node.Id}); work cannot go back up the chain.");
        if (requester is not null && requester.Slug == assignee.Slug) return EnqueueResult.Refuse(EnqueueReasons.Ancestor, "A bot cannot hand work to itself.");

        var depth = parent is null ? 0 : parent.Depth + 1;
        if (depth > h.MaxDepth) return EnqueueResult.Refuse(EnqueueReasons.Depth, $"Depth {depth} is over the limit of {h.MaxDepth}.");

        AssignmentRow? root = parent is null ? null : Store.Get(parent.RootId);
        if (root is not null)
        {
            var tree = Store.Tree(root.Id);
            if (tree.Sum(x => x.TokensUsed) >= root.BudgetTokens) return EnqueueResult.Refuse(EnqueueReasons.Budget, "The tree has used its token budget.");
            if (tree.Sum(x => x.Executions) >= root.BudgetTurns) return EnqueueResult.Refuse(EnqueueReasons.Budget, "The tree has used its turn budget.");
            if (root.Deadline is { } dl && now >= dl) return EnqueueResult.Refuse(EnqueueReasons.Budget, "The tree is past its deadline.");
            if (tree.Count(x => !x.IsTerminal) >= h.MaxLivePerTree) return EnqueueResult.Refuse(EnqueueReasons.TreeFull, $"The tree already holds {h.MaxLivePerTree} live assignments.");
            if (parent is not null && Store.Children(parent.Id).Count(c => c.RequesterTurn == r.RequesterTurn && c.JoinedTo is null) >= h.MaxChildrenPerTurn)
                return EnqueueResult.Refuse(EnqueueReasons.Fanout, $"A turn may hand out at most {h.MaxChildrenPerTurn} assignments.");
        }

        var origin = parent?.Origin ?? (r.Origin is "person" or "routine" or "outside" or "roster" or "room" ? r.Origin : "outside");
        if (requester is not null && r.RequesterKind == "bot" && !assignee.Spec.AcceptsFrom.Any(a => Names.SameName(a, requester.Name) || a == "*"))
            return EnqueueResult.Refuse(EnqueueReasons.Refused, $"{assignee.Name} does not accept work from {requester.Name} (accepts_from).");

        // The child's level is the lower of the requester's level and the assignee's own profile (plan 7.5 check 7).
        var requesterLevel = parent?.Level ?? (origin == "person" ? assignee.Spec.PermissionProfile : "ask");
        var level = MinLevel(requesterLevel, assignee.Spec.PermissionProfile);
        if (r.RequestedLevel is { } want)
        {
            if (Array.IndexOf(Profiles, want) < 0) return EnqueueResult.Refuse(EnqueueReasons.Invalid, "level must be ask, edits or auto.");
            if (Rank(want) > Rank(level)) return EnqueueResult.Refuse(EnqueueReasons.Level, $"A child never runs above its requester's level ({level}).");
            level = want;
        }

        var key = DedupeKey(r.Brief, assignee.Slug, parent?.Id, r.Mode);
        var windowStart = AssignmentStore.Iso(now - TimeSpan.FromMinutes(h.DedupeWindowMinutes));
        if (!r.Rework)
        {
            var same = _db.Scalar("SELECT id FROM assignments WHERE dedupe_key=$k AND created_at >= $w AND status NOT IN ('failed','cancelled') ORDER BY created_at LIMIT 1;",
                ("$k", key), ("$w", windowStart)) as string;
            if (same is not null) return new EnqueueResult(true, same, EnqueueReasons.Joined, $"The same request is already {same}; this joins it.");
        }
        string? joinedTo = null;
        var norm = Normalize(r.Brief);
        if (!r.Rework && norm.Length >= h.SameRequestMinChars)
        {
            joinedTo = _db.Query("""
                SELECT id, brief, done_when, parent_id FROM assignments WHERE assignee_bot=$b AND mode=$m AND joined_to IS NULL AND created_at >= $w
                AND status NOT IN ('completed','failed','cancelled') ORDER BY created_at;
                """, x => (Id: x.GetString(0), Brief: x.GetString(1), Done: RosterDb.Str(x, "done_when"), Parent: RosterDb.Str(x, "parent_id")),
                ("$b", assignee.Slug), ("$m", r.Mode), ("$w", windowStart))
                .FirstOrDefault(x => x.Parent != parent?.Id && Normalize(x.Brief) == norm && (x.Done ?? "") == (r.DoneWhen ?? "")).Id;
        }

        var reply = r.Reply ?? (r.Mode == "notify" ? "none" : parent is null ? "poll" : "resume");
        var session = r.Session ?? (r.Mode == "work" && parent is null && r.RequesterKind == "person" ? "standing" : "fresh");
        var priority = r.Priority ?? (origin == "routine" ? Priority.Routine : parent is null && origin == "person" ? Priority.Interactive : Priority.Assignment);
        var id = AssignmentStore.NewId();
        var status = joinedTo is not null ? AssignmentStatus.Waiting : r.ClaimWith is not null ? AssignmentStatus.Running : AssignmentStatus.Queued;
        var row = new AssignmentRow(
            id, root?.Id ?? id, parent?.Id, depth, origin, r.OriginRef, r.RequesterKind, requester?.Slug, r.RequesterSession, r.RequesterTurn,
            assignee.Slug, session, null, r.Mode, reply, r.Brief, r.DoneWhen, r.Input, key, r.Rework, joinedTo, status, null, null, level,
            root?.BudgetTokens ?? h.TreeTokenBudget, root?.BudgetTurns ?? h.TreeTurnBudget, root?.Deadline ?? now + TimeSpan.FromMinutes(r.DeadlineMinutes ?? h.TreeDeadlineMinutes),
            0, 0, 0, null, 0, null, null, Priorities.Name(priority), r.RoutineRunId, r.ClaimWith?.Owner, r.TurnMessage, false, now, now,
            r.ClaimWith is null ? null : now, null);
        _db.InTransaction((c, tx) =>
        {
            Store.Insert(c, tx, row, r.Actor, joinedTo is null ? "enqueued" : $"joined {joinedTo}: the same request is being answered", new
            {
                mode = r.Mode, level, depth, assignee = assignee.Slug, requester = requester?.Slug ?? r.RequesterKind, route = route.How,
            });
            if (joinedTo is null) Store.AddInbox(c, tx, assignee.Slug, id, "new_work", new { from = requester?.Slug ?? r.RequesterKind, mode = r.Mode });
            _audit.Write(c, tx, r.Actor, "assignment.enqueue", id, $"{r.Mode} to {assignee.Slug} depth {depth} level {level}{(joinedTo is null ? "" : " joined " + joinedTo)}");
        });
        if (r.ClaimWith is not null) Turns.SetAssignment(r.ClaimWith, id);
        return joinedTo is null
            ? new EnqueueResult(true, id, EnqueueReasons.Ok, $"Handed to {assignee.Name} as {id} ({r.Mode}).")
            : new EnqueueResult(true, id, EnqueueReasons.Joined, $"{assignee.Name} is already answering the same request ({joinedTo}); {id} waits for that answer.");
    }

    // ---------------------------------------------------------------- dispatch

    /// <summary>
    /// One service tick: expire trees past their deadline, deliver answers to
    /// parked questions, wake parents whose children settled, then admit queued
    /// work while bots are free and slots are open.
    /// </summary>
    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var now = _clock.UtcNow;
        foreach (var root in Store.ByStatus(AssignmentStatus.Live).Where(a => a.IsRoot && a.Deadline is { } d && d <= now))
            lines.Add($"{root.Id}: past its deadline; {FailTree(root.Id, Outcomes.Expired, "the tree passed its deadline")} assignment(s) failed");
        lines.AddRange(DeliverAnswers());
        foreach (var w in Store.ByStatus(AssignmentStatus.Waiting).Where(a => a.JoinedTo is null)) TryWake(w);
        var ready = Store.ByStatus(AssignmentStatus.Queued, AssignmentStatus.Resume)
            .Where(a => a.JoinedTo is null)
            .OrderBy(a => (int)Priorities.Parse(a.Priority)).ThenBy(a => a.CreatedAt).ToList();
        foreach (var a in ready)
        {
            if (_inflight.ContainsKey(a.Id) || Turns.Busy(a.AssigneeBot) || !_slotFree(a.AssigneeBot)) continue;
            if (a.Status == AssignmentStatus.Queued && Store.Tree(a.RootId).Sum(x => x.Executions) >= a.BudgetTurns)
            {
                Settle(a, AssignmentStatus.Failed, Outcomes.Budget, "the tree used its turn budget before this could start", null);
                continue;
            }
            var bot = _bots.Find(a.AssigneeBot);
            if (bot is null || !bot.Enabled)
            {
                Settle(a, AssignmentStatus.Failed, Outcomes.Refused, $"{a.AssigneeBot} is missing or disabled", null);
                continue;
            }
            // A bot the health monitor is restarting keeps its accepted work queued instead of failing it (SOAK-02).
            if (bot.State is Supervision.BotStates.Failed or Supervision.BotStates.Starting) continue;
            var lease = Turns.TryAcquire(a.AssigneeBot, Priorities.Parse(a.Priority), a.RoutineRunId is null ? null : "routine-run-" + a.RoutineRunId, TurnTtl(bot), a.Id);
            if (lease is null) continue;
            var from = a.Status;
            if (!Store.Transition(a.Id, [AssignmentStatus.Queued, AssignmentStatus.Resume], AssignmentStatus.Running, "roster", "admitted",
                    new Dictionary<string, object?> { ["lease_owner"] = lease.Owner, ["started_at"] = AssignmentStore.Iso(_clock.UtcNow) }))
            {
                Turns.Release(lease);
                continue;
            }
            var task = Task.Run(async () =>
            {
                try { await ExecuteAsync(a.Id, from, lease, null, null, ct).ConfigureAwait(false); }
                finally { Turns.Release(lease); }
            }, CancellationToken.None);
            _inflight[a.Id] = task;
            _ = task.ContinueWith(_ => _inflight.TryRemove(a.Id, out var _), TaskScheduler.Default);
            lines.Add($"{a.Id}: admitted to {a.AssigneeBot} ({a.Mode}{(from == AssignmentStatus.Resume ? ", resume" : "")})");
        }
        return lines;
    }

    private TimeSpan TurnTtl(BotRow bot) => TimeSpan.FromMinutes(bot.Spec.MaxTurnMinutes + 2);

    /// <summary>
    /// Runs the turn of an assignment the caller already claimed (status
    /// running, holding <paramref name="lease"/>): the CLI for a person's
    /// message and the routine runner. Returns the last turn report and the row.
    /// </summary>
    public async Task<(TurnReport? Report, AssignmentRow Row)> RunNowAsync(string id, TurnLease lease, string? mode, Action<SseEvent>? onEvent, CancellationToken ct, TimeSpan? timeout = null)
    {
        Turns.SetAssignment(lease, id);
        var report = await ExecuteAsync(id, AssignmentStatus.Queued, lease, mode, onEvent, ct, timeout).ConfigureAwait(false);
        return (report, Store.Get(id)!);
    }

    private IReadOnlyList<string> Blocks(AssignmentRow a, BotSpec spec)
    {
        var blocks = new HashSet<string>(StringComparer.Ordinal);
        if (a.Mode is "consult" or "notify") blocks.UnionWith(TurnGate.ConsultBlocks(spec));
        if (a.Origin is "routine" or "outside" || a.Level == "ask") blocks.UnionWith(TurnGate.RoutineBlocks(spec));
        return [.. blocks.OrderBy(x => x, StringComparer.Ordinal)];
    }

    private static string GateOrigin(AssignmentRow a) => a.Mode is "consult" or "notify" ? a.Mode : a.IsRoot ? a.Origin : a.Origin == "outside" ? "outside" : "assignment";

    private string RequesterLabel(AssignmentRow a)
        => a.RequesterBot is { } rb ? $"{_bots.Find(rb)?.Name ?? rb} (bot)"
            : a.RequesterKind == "routine" ? $"routine {a.OriginRef}"
            : a.RequesterKind == "roster" ? "the roster (approval broker)"
            : a.RequesterKind == "room" ? $"room {a.OriginRef}"
            : a.Origin == "outside" ? "an outside source" : "Spencer (person)";

    private async Task<TurnReport?> ExecuteAsync(string id, string from, TurnLease lease, string? modeOverride, Action<SseEvent>? onEvent, CancellationToken ct, TimeSpan? timeout = null)
    {
        var a = Store.Get(id)!;
        var bot = _bots.Find(a.AssigneeBot);
        if (bot is null)
        {
            Settle(a, AssignmentStatus.Failed, Outcomes.Refused, "the bot no longer exists", null);
            return null;
        }
        // A resume carries what is new since the last turn: a question first (it needs an answer), then results not yet read.
        var unread = from == AssignmentStatus.Resume ? Store.UnreadReports(a.Id) : [];
        var parts = new List<string>();
        if (a.Executions == 0 && a.IsRoot && a.Note is { Length: > 0 } first) parts.Add(first);                       // a person's or routine's exact opening
        else
        {
            if (a.Note is { Length: > 0 } note) parts.Add(note);                                                      // a question, an answer, a continuation or a restart note
            if (unread.Count > 0) parts.Add(Envelope.Report(a, unread, null));
        }
        var message = parts.Count > 0
            ? string.Join("\n\n", parts)
            : Envelope.Assignment(a, RequesterLabel(a), _config.Handoffs.MaxDepth, Math.Max(0, a.BudgetTokens - Store.Tree(a.RootId).Sum(x => x.TokensUsed)));
        if (unread.Count > 0) Store.MarkChildReportsDelivered(a.Id);
        var blocks = Blocks(a, bot.Spec);
        var mode = modeOverride ?? (a.Mode == "consult" ? "plan" : a.SessionId is null && !(a.IsRoot && a.Origin == "person") ? "build" : null);
        var session = a.SessionId is { } sid ? "id:" + sid : a.AssigneeSession;
        Store.Update(a.Id, "roster", "dispatched", new Dictionary<string, object?> { ["executions"] = a.Executions + 1, ["note"] = null },
            new { level = a.Level, mode = mode ?? "keep", blocks, session });
        var started = _clock.UtcNow;
        var limit = timeout ?? TimeSpan.FromMinutes(bot.Spec.MaxTurnMinutes);
        Turns.Extend(lease, limit + TimeSpan.FromMinutes(2));
        var observe = Observer(a.Id, bot.Slug, lease, onEvent);
        try
        {
            var report = await _driver.RunAsync(new TurnRequest(bot.Slug, message, GateOrigin(a), session, mode, blocks, limit, observe), ct).ConfigureAwait(false);
            return await AfterTurnAsync(a.Id, report, lease, started, ct).ConfigureAwait(false);
        }
        finally
        {
            _observers.TryRemove(a.Id, out _);
        }
    }

    private readonly ConcurrentDictionary<string, Action<SseEvent>> _observers = new(StringComparer.Ordinal);

    /// <summary>Records outward tool starts for the restart policy and passes events on. Kept per assignment so a resumed pause streams the same way.</summary>
    private Action<SseEvent> Observer(string id, string botSlug, TurnLease lease, Action<SseEvent>? onEvent)
    {
        void Observe(SseEvent e)
        {
            if (e.Name == "tool_start" && ToolStart(e.Data) is { } tool && ToolCatalog.IsOutward(tool.Name))
                Store.RecordOutward(botSlug, id, Store.Get(id)?.SessionId, lease.Owner, tool.Name, $"{tool.Name}: {tool.Summary}", "started");
            onEvent?.Invoke(e);
        }
        _observers[id] = Observe;
        return Observe;
    }

    /// <summary>
    /// Everything after a turn's stream ended: token accounting, pauses (the
    /// broker answers them or parks the work in needs_input with the pause
    /// still open), the step limit continuation and the final outcome.
    /// </summary>
    private async Task<TurnReport> AfterTurnAsync(string id, TurnReport report, TurnLease lease, DateTimeOffset started, CancellationToken ct)
    {
        Account(id, report, started);
        while (report.End == TurnEnd.Paused && report.Pause is not null)
        {
            var current = Store.Get(id)!;
            if (current.IsTerminal) return report;
            var outcome = await Pauses.HandleAsync(current, report, lease, ct).ConfigureAwait(false);
            if (outcome.Continued is null)
            {
                Store.Transition(id, [AssignmentStatus.Running], AssignmentStatus.NeedsInput, "roster", outcome.ParkedReason ?? "waiting for a person",
                    new Dictionary<string, object?> { ["lease_owner"] = null });
                return report;
            }
            report = outcome.Continued;
            Account(id, report, started);
        }
        if (Store.Get(id) is not { Status: AssignmentStatus.Running }) return report;
        if (report.End == TurnEnd.Done && report.Text.Contains(Routines.AcpRoutineRunner.StepCapMarker, StringComparison.Ordinal) && !Stalled(id, report))
        {
            Store.Transition(id, [AssignmentStatus.Running], AssignmentStatus.Queued, "roster", "continue: the turn stopped at its step limit",
                new Dictionary<string, object?> { ["continuations"] = Store.Get(id)!.Continuations + 1, ["note"] = Routines.AcpRoutineRunner.ContinueMessage, ["lease_owner"] = null });
            return report;
        }
        if (Store.Get(id) is { Status: AssignmentStatus.Running } row) Finish(row, report);
        return report;
    }

    /// <summary>
    /// Answers an open pause of an assignment's turn and reads the rest of
    /// the turn, with the same gate, time limit and event handling as the
    /// turn that paused. Only the approval broker calls this.
    /// </summary>
    public Task<TurnReport> AnswerPauseAsync(AssignmentRow a, AcpPause pause, string session, bool allow, string? value, CancellationToken ct)
    {
        var bot = _bots.Find(a.AssigneeBot);
        if (bot is null) return Task.FromResult(TurnReport.Unavailable("the bot no longer exists", session));
        var limit = TimeSpan.FromMinutes(bot.Spec.MaxTurnMinutes);
        var observe = _observers.TryGetValue(a.Id, out var o) ? o : null;
        return _driver.ResumeAsync(bot.Slug, session, pause, allow, value, GateOrigin(a), Blocks(a, bot.Spec), limit, observe, ct);
    }

    /// <summary>
    /// Picks up work parked in needs_input once its answer is ready: claims
    /// the bot's turn lease, moves the work back to running and runs
    /// <paramref name="resume"/> (which answers the open pause) in the
    /// background, then the normal post-turn handling. False when the bot is
    /// busy or the work already moved on; the caller tries again next tick.
    /// </summary>
    public bool ContinueParked(string id, string reason, Func<AssignmentRow, TurnLease, CancellationToken, Task<TurnReport?>> resume, CancellationToken ct)
    {
        var a = Store.Get(id);
        if (a is null || a.Status != AssignmentStatus.NeedsInput || _inflight.ContainsKey(id)) return false;
        var bot = _bots.Find(a.AssigneeBot);
        if (bot is null || Turns.Busy(a.AssigneeBot)) return false;
        var lease = Turns.TryAcquire(a.AssigneeBot, Priorities.Parse(a.Priority), a.RoutineRunId is null ? null : "routine-run-" + a.RoutineRunId, TurnTtl(bot), a.Id);
        if (lease is null) return false;
        if (!Store.Transition(id, [AssignmentStatus.NeedsInput], AssignmentStatus.Running, "roster", reason, new Dictionary<string, object?> { ["lease_owner"] = lease.Owner }))
        {
            Turns.Release(lease);
            return false;
        }
        var task = Task.Run(async () =>
        {
            try
            {
                Observer(id, bot.Slug, lease, null);
                var started = _clock.UtcNow;
                var report = await resume(Store.Get(id)!, lease, ct).ConfigureAwait(false);
                if (report is not null) await AfterTurnAsync(id, report, lease, started, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (Store.Get(id) is { } row && !row.IsTerminal)
                    Settle(row, AssignmentStatus.Failed, Outcomes.Error, "the answered turn failed: " + Redactor.Shared.Redact(ex.Message), row.Result);
            }
            finally
            {
                _observers.TryRemove(id, out _);
                Turns.Release(lease);
            }
        }, CancellationToken.None);
        _inflight[id] = task;
        _ = task.ContinueWith(_ => _inflight.TryRemove(id, out var _), TaskScheduler.Default);
        return true;
    }

    private static (string Name, string Summary)? ToolStart(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var summary = doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            return name.Length == 0 ? null : (name, summary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Account(string id, TurnReport report, DateTimeOffset started)
    {
        var a = Store.Get(id)!;
        var gateway = Convert.ToInt64(_db.Scalar("SELECT COALESCE(SUM(prompt_tokens + completion_tokens), 0) FROM usage WHERE assignment=$a AND at >= $s;", ("$a", id), ("$s", started.ToString("O"))) ?? 0L);
        var used = Math.Max(gateway, report.Usage?.TotalTokens ?? 0);
        Store.Update(id, "roster", "turn accounted", new Dictionary<string, object?>
        {
            ["tokens_used"] = a.TokensUsed + used,
            ["session_id"] = report.SessionId ?? a.SessionId,
            ["evidence"] = report.Evidence.Summary(),
        });
    }

    /// <summary>Stall guards (plan 7.8): two continuations without a successful tool call, or three identical errors, end the work.</summary>
    private bool Stalled(string id, TurnReport report)
    {
        var a = Store.Get(id)!;
        var guard = a.Guard is null ? new Guard(0, []) : JsonSerializer.Deserialize<Guard>(a.Guard) ?? new Guard(0, []);
        var noProgress = report.Evidence.OkTools == 0 ? guard.NoProgress + 1 : 0;
        var errors = guard.Errors.Concat(report.Evidence.Errors).TakeLast(3).ToList();
        Store.Update(id, "roster", "guard", new Dictionary<string, object?> { ["guard"] = JsonSerializer.Serialize(new Guard(noProgress, errors)) });
        if (noProgress >= 2)
        {
            Settle(a, AssignmentStatus.Failed, Outcomes.Stalled, "stopped after 2 continuations without a successful tool call", report.Text);
            return true;
        }
        if (errors.Count == 3 && errors.Distinct().Count() == 1)
        {
            Settle(a, AssignmentStatus.Failed, Outcomes.Stalled, "stopped after 3 identical errors: " + errors[0], report.Text);
            return true;
        }
        return false;
    }

    private sealed record Guard(int NoProgress, List<string> Errors);

    private void Finish(AssignmentRow a, TurnReport report)
    {
        if (report.TimedOut) { Settle(a, AssignmentStatus.Failed, Outcomes.Aborted, "the turn ran past its time limit and was aborted", report.Text); return; }
        if (report.Problem is not null) { Settle(a, AssignmentStatus.Failed, Outcomes.Error, report.Problem, null); return; }
        switch (report.End)
        {
            case TurnEnd.Done:
                if (a.Mode == "consult" && a.Executions > _config.Handoffs.ConsultMaxTurns)
                {
                    Settle(a, AssignmentStatus.Failed, Outcomes.Budget, "a consultation gets at most " + _config.Handoffs.ConsultMaxTurns + " turns", report.Text);
                    return;
                }
                var openAsk = Approvals.Pending().Any(p => p.AssignmentId == a.Id && p.Kind == ApprovalKinds.Ask);
                var openQuestion = Store.Inbox(null, true).Any(i => i.AssignmentId == a.Id && i.Kind == "child_question");
                if (openAsk || openQuestion)
                {
                    Store.Transition(a.Id, [AssignmentStatus.Running], AssignmentStatus.NeedsInput, "roster", openAsk ? "asked Spencer a question" : "asked its requester a question",
                        new Dictionary<string, object?> { ["result"] = Cap(report.Text), ["lease_owner"] = null });
                    if (openQuestion && a.ParentId is not null && Store.Get(a.ParentId) is { Status: AssignmentStatus.Waiting } parent)
                        Store.Transition(parent.Id, [AssignmentStatus.Waiting], AssignmentStatus.Resume, "roster", $"{a.Id} asked a question",
                            new Dictionary<string, object?> { ["note"] = $"[question from {a.Id} ({a.AssigneeBot})] Answer it with the roster answer tool.\n{Envelope.Fenced("Question", QuestionOf(a.Id))}" });
                    return;
                }
                var waitingOn = Store.Children(a.Id).Count(c => c.Reply == "resume" && !c.IsTerminal);
                if (waitingOn > 0)
                {
                    Store.Transition(a.Id, [AssignmentStatus.Running], AssignmentStatus.Waiting, "roster", $"waiting on {waitingOn} assignment(s)",
                        new Dictionary<string, object?> { ["result"] = Cap(report.Text), ["lease_owner"] = null });
                    TryWake(Store.Get(a.Id)!);
                    return;
                }
                Settle(a, AssignmentStatus.Completed, Outcomes.Ok, null, report.Text);
                return;
            case TurnEnd.Busy:
                Settle(a, AssignmentStatus.Failed, Outcomes.Error, "the session was busy", report.Text);
                return;
            default:
                Settle(a, AssignmentStatus.Failed, Outcomes.Error, report.Error ?? report.End.ToString(), report.Text);
                return;
        }
    }

    private string QuestionOf(string id)
        => Store.Inbox(null, true).Where(i => i.AssignmentId == id && i.Kind == "child_question").Select(i =>
        {
            using var doc = JsonDocument.Parse(i.Payload);
            return doc.RootElement.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
        }).LastOrDefault() ?? "";

    private string? Cap(string? text) => text is null ? null : text.Length <= _config.Handoffs.ResultMaxChars ? text : text[.._config.Handoffs.ResultMaxChars] + "\n(cut)";

    // ---------------------------------------------------------------- settle and wake

    /// <summary>
    /// Writes a terminal outcome exactly once, then reports it once to the
    /// requester (a child_done inbox row) and settles requests joined to it.
    /// </summary>
    public bool Settle(AssignmentRow a, string status, string outcome, string? detail, string? result)
    {
        var settled = _db.InTransaction((c, tx) =>
        {
            if (!Store.Transition(c, tx, a.Id, AssignmentStatus.Live, status, "roster", detail ?? outcome,
                    new Dictionary<string, object?> { ["outcome"] = outcome, ["outcome_detail"] = detail, ["result"] = Cap(result), ["lease_owner"] = null }))
                return false;
            Report(c, tx, Store.Get(c, tx, a.Id)!);
            foreach (var j in Store.JoinedTo(a.Id).Where(j => !j.IsTerminal))
            {
                if (Store.Transition(c, tx, j.Id, AssignmentStatus.Live, status, "roster", $"answered once for both by {a.Id}",
                        new Dictionary<string, object?> { ["outcome"] = outcome, ["outcome_detail"] = $"Another bot asked the same a moment ago, so it was answered once for both ({a.Id}).", ["result"] = Cap(result) }))
                    Report(c, tx, Store.Get(c, tx, j.Id)!);
            }
            return true;
        });
        if (!settled) return false;
        foreach (var p in Approvals.Pending().Where(p => p.AssignmentId == a.Id)) Approvals.Cancel(p.Id, $"assignment {a.Id} ended {status}");
        var final = Store.Get(a.Id)!;
        _audit.Write("roster", "assignment." + status, a.Id, $"{outcome}{(detail is null ? "" : ": " + detail)}");
        if (a.ParentId is not null && Store.Get(a.ParentId) is { } parent) TryWake(parent);
        foreach (var j in Store.JoinedTo(a.Id)) if (j.ParentId is not null && Store.Get(j.ParentId) is { } jp) TryWake(jp);
        if (final.IsRoot && status != AssignmentStatus.Completed)
            _notices.Add("error", a.AssigneeBot, $"Work {a.Id} for {a.AssigneeBot} ended {status} ({outcome}){(detail is null ? "" : ": " + detail)}. See oma-bot audit tree {a.RootId}.", "assignment:" + a.Id);
        return true;
    }

    private void Report(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, AssignmentRow a)
    {
        if (a.Reported) return;
        if (a.ParentId is not null && a.Reply is "resume" or "poll")
        {
            var parent = Store.Get(c, tx, a.ParentId);
            if (parent is not null) Store.AddInbox(c, tx, parent.AssigneeBot, a.Id, "child_done", new { child = a.Id, status = a.Status, outcome = a.Outcome });
        }
        Store.Transition(c, tx, a.Id, [a.Status], a.Status, "roster", "reported", new Dictionary<string, object?> { ["reported"] = 1 });
    }

    /// <summary>A waiting parent goes to resume once every child it waits on has settled (one wake per result, I9).</summary>
    public void TryWake(AssignmentRow parent)
    {
        if (parent.Status != AssignmentStatus.Waiting || parent.JoinedTo is not null) return;
        var children = Store.Children(parent.Id).Where(c => c.Reply == "resume").ToList();
        if (children.Count == 0 || children.Any(c => !c.IsTerminal)) return;
        Store.Transition(parent.Id, [AssignmentStatus.Waiting], AssignmentStatus.Resume, "roster", "all children settled");
    }

    /// <summary>Answers to parked questions: a person's answer to an ask card, or a requester's answer to a child's question.</summary>
    private IReadOnlyList<string> DeliverAnswers()
    {
        var lines = new List<string>();
        foreach (var card in Approvals.Undelivered().Where(x => x.Kind == ApprovalKinds.Ask && x.AssignmentId is not null))
        {
            var a = Store.Get(card.AssignmentId!);
            if (a is null || a.Status != AssignmentStatus.NeedsInput)
            {
                if (a is null || a.IsTerminal) Approvals.MarkDelivered(card.Id, "the assignment already ended");
                continue;
            }
            var payload = card.PayloadJson();
            var answer = payload["answer"]?.GetValue<string>();
            string note;
            if (payload["pause_expired"]?.GetValue<string>() is { } expired)
            {
                var action = payload["action"]?.GetValue<string>() ?? "";
                note = $"[roster] Your request ({expired}) expired with no answer (pause_expired), so that turn was stopped and nothing of it ran.\n"
                       + Envelope.Fenced("Request", action)
                       + (card.IsAllowed
                           ? $"Spencer has now answered:\n{Envelope.Fenced("Answer", answer ?? "(no text)")}Continue your work; ask again for the action if you still need it."
                           : $"Spencer said no{(card.Reason is null ? "" : ": " + Envelope.Escape(card.Reason))}. Continue without it, or stop and say what you need.");
            }
            else
            {
                note = card.IsAllowed
                    ? $"[answer from Spencer to your question ({card.Id})]\n{Envelope.Fenced("Answer", answer ?? "(no text)")}Continue your work."
                    : $"[Spencer declined your question ({card.Id})]{(card.Reason is null ? "" : " Reason: " + Envelope.Escape(card.Reason))}\nContinue without it, or stop and say what you need.";
            }
            if (Store.Transition(a.Id, [AssignmentStatus.NeedsInput], AssignmentStatus.Resume, "person", "question answered", new Dictionary<string, object?> { ["note"] = note }))
            {
                Approvals.MarkDelivered(card.Id, "answer delivered to " + a.Id);
                lines.Add($"{a.Id}: answer delivered");
            }
        }
        return lines;
    }

    /// <summary>A requester answers its child's question (the answer tool). Only the child's own requester may.</summary>
    public (bool Ok, string Message) AnswerChild(string childId, string answeringBot, string text)
    {
        var child = Store.Get(childId);
        if (child is null) return (false, $"No assignment {childId}.");
        var parent = child.ParentId is null ? null : Store.Get(child.ParentId);
        if (parent is null || parent.AssigneeBot != answeringBot) return (false, "Only the bot that handed out this work can answer its question.");
        if (child.Status != AssignmentStatus.NeedsInput || !Store.Inbox(null, true).Any(i => i.AssignmentId == childId && i.Kind == "child_question"))
            return (false, $"{childId} has no open question for you.");
        var note = $"[answer from your requester {answeringBot} for {childId}]\n{Envelope.Fenced("Answer", text)}Continue your work.";
        if (!Store.Transition(childId, [AssignmentStatus.NeedsInput], AssignmentStatus.Resume, "bot:" + answeringBot, "requester answered", new Dictionary<string, object?> { ["note"] = note }))
            return (false, "The question was answered already.");
        Store.MarkInboxDelivered(childId, "child_question");
        return (true, $"Answered {childId}.");
    }

    /// <summary>A child asks its requester a question (the ask tool with to: requester).</summary>
    public (bool Ok, string Message) AskRequester(string assignmentId, string question)
    {
        var a = Store.Get(assignmentId);
        if (a?.ParentId is null) return (false, "This work has no requester bot to ask; ask Spencer instead.");
        var parent = Store.Get(a.ParentId)!;
        _db.InTransaction((c, tx) => Store.AddInbox(c, tx, parent.AssigneeBot, assignmentId, "child_question", new { child = assignmentId, question = Redactor.Shared.Redact(question) }));
        return (true, $"Asked {parent.AssigneeBot}. End your turn; the answer comes in a later turn.");
    }

    // ---------------------------------------------------------------- cancel, expire, reconcile

    /// <summary>Cancel cascades down the tree and aborts running turns; it is itself a reported outcome.</summary>
    public async Task<CancelResult> CancelAsync(string id, string actor, string reason, CancellationToken ct = default)
    {
        var a = Store.Get(id);
        if (a is null) return new CancelResult(false, $"No assignment {id}.", 0);
        var nodes = new List<AssignmentRow>();
        void Walk(AssignmentRow n)
        {
            foreach (var c in Store.Children(n.Id)) Walk(c);
            nodes.Add(n);
        }
        Walk(a);
        var n = 0;
        foreach (var node in nodes.Where(x => !x.IsTerminal))
        {
            if (node.Status == AssignmentStatus.Running && node.SessionId is { } sid)
                await _driver.EndPausedAsync(node.AssigneeBot, sid, ct).ConfigureAwait(false);
            if (Settle(node, AssignmentStatus.Cancelled, Outcomes.Aborted, $"cancelled by {actor}: {reason}", node.Result)) n++;
        }
        return new CancelResult(n > 0, n > 0 ? $"Cancelled {n} assignment(s)." : $"{id} had already ended.", n);
    }

    public int FailTree(string rootId, string outcome, string detail)
    {
        var n = 0;
        foreach (var node in Store.Tree(rootId).Where(x => !x.IsTerminal).OrderByDescending(x => x.Depth))
            if (Settle(node, AssignmentStatus.Failed, outcome, detail, node.Result)) n++;
        return n;
    }

    /// <summary>
    /// At service start (plan 7.5 restart policy): a turn cut by the restart is
    /// never repeated if it already acted outward; otherwise it reruns once.
    /// A turn that had handed out work waits for that work instead.
    /// </summary>
    public IReadOnlyList<string> Reconcile()
    {
        var lines = new List<string>();
        foreach (var a in Store.ByStatus(AssignmentStatus.Running))
        {
            if (a.LeaseOwner is not null) _db.Exec("DELETE FROM turn_context WHERE owner=$o;", ("$o", a.LeaseOwner));
            var since = a.StartedAt ?? a.CreatedAt;
            var children = Store.Children(a.Id).Where(c => c.CreatedAt >= since && c.Reply == "resume").ToList();
            if (children.Count > 0)
            {
                Store.Transition(a.Id, [AssignmentStatus.Running], AssignmentStatus.Waiting, "roster", "restart_reconciled: the turn was cut after it handed out work; it waits for that work",
                    new Dictionary<string, object?> { ["lease_owner"] = null, ["note"] = "[roster] A roster restart cut your last turn after you handed out work. Nothing of it runs again." });
                TryWake(Store.Get(a.Id)!);
                lines.Add($"{a.Id}: waits for its {children.Count} assignment(s) after a restart");
                continue;
            }
            var outward = OutwardSince(a, since);
            if (outward.Count > 0)
            {
                Settle(a, AssignmentStatus.Failed, Outcomes.Interrupted, $"Interrupted by a roster restart after it already did: {string.Join("; ", outward)}. Not resumed, so nothing is done twice.", a.Result);
                lines.Add($"{a.Id}: failed (interrupted after an outward action)");
            }
            else if (a.Restarts == 0)
            {
                Store.Transition(a.Id, [AssignmentStatus.Running], AssignmentStatus.Queued, "roster", "restart_reconciled: rerun once",
                    new Dictionary<string, object?> { ["restarts"] = 1, ["lease_owner"] = null, ["note"] = "[roster] A roster restart cut your last turn on this work. Check what you already did before you continue.\n" + Envelope.Assignment(a, RequesterLabel(a), _config.Handoffs.MaxDepth, a.BudgetTokens) });
                lines.Add($"{a.Id}: queued to rerun once after a restart");
            }
            else
            {
                Settle(a, AssignmentStatus.Failed, Outcomes.Error, "cut by a roster restart a second time", a.Result);
                lines.Add($"{a.Id}: failed (cut twice)");
            }
        }
        return lines;
    }

    /// <summary>Outward actions a cut turn already started: roster records and the pre tool hook's marker file.</summary>
    public IReadOnlyList<string> OutwardSince(AssignmentRow a, DateTimeOffset since)
    {
        var list = Store.Outward(a.Id, since).Select(o => o.Label).ToList();
        var marker = Path.Combine(_paths.Bot(a.AssigneeBot).Data, "roster-flags", "outward.log");
        if (File.Exists(marker) && new FileInfo(marker).LinkTarget is null)
        {
            foreach (var line in File.ReadLines(marker))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                    && DateTimeOffset.FromUnixTimeSeconds(epoch) >= since.AddSeconds(-1) && Names.IsToolName(parts[1].Trim()))
                    list.Add(parts[1].Trim() + " (hook record)");
            }
        }
        return list;
    }
}

/// <summary>
/// The phase 5 pause handling: a pause becomes an approval card and the
/// assignment waits for a person (needs_input). The turn in the bot is ended,
/// so nothing runs on until someone answers. Phase 6 replaces this with the
/// approval broker, which answers the paused turn itself.
/// </summary>
public sealed class ParkingPauseHandler(AssignmentRuntime runtime) : IPauseHandler
{
    public async Task<PauseOutcome> HandleAsync(AssignmentRow a, TurnReport report, TurnLease lease, CancellationToken ct)
    {
        var pause = report.Pause!;
        runtime.Approvals.Create(new ApprovalRequest(PauseCards.KindOf(pause.Kind), a.AssigneeBot, $"{a.AssigneeBot}: {pause.Summary}",
            new System.Text.Json.Nodes.JsonObject { ["pause"] = pause.Kind.ToString(), ["payload"] = System.Text.Json.Nodes.JsonNode.Parse(pause.Payload.GetRawText()) },
            a.RequesterKind == "bot" ? "bot" : a.RequesterKind, a.AssigneeBot, a.Origin, report.SessionId, pause.Id, a.Id, a.RootId));
        if (report.SessionId is { } sid) await runtime.Driver.EndPausedAsync(a.AssigneeBot, sid, ct).ConfigureAwait(false);
        return new PauseOutcome(null, $"paused for {pause.Kind}: {Redactor.Shared.Redact(pause.Summary)}");
    }
}

public static class PauseCards
{
    public static string KindOf(PauseKind k) => k switch
    {
        PauseKind.Permission => ApprovalKinds.Permission,
        PauseKind.Playbook => ApprovalKinds.Playbook,
        PauseKind.ToggleMode => ApprovalKinds.ToggleMode,
        PauseKind.HostExec => ApprovalKinds.HostExec,
        PauseKind.Plan => ApprovalKinds.Plan,
        _ => ApprovalKinds.UserInput,
    };
}
