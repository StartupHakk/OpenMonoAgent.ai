using System.Collections.Concurrent;
using OpenMono.Session;

namespace OpenMono.Acp;

public sealed class AcpSession
{
    public required SessionState State { get; init; }

    public DateTime LastActivityAt { get; set; }

    public string Id => State.Id;
    public DateTime StartedAt => State.StartedAt;
    public string Model => State.Model ?? "";
    public List<TodoItem> Todos => State.Todos;
    public List<Message> Messages => State.Messages;

    public int TurnCount
    {
        get => State.TurnCount;
        set => State.TurnCount = value;
    }

    public bool PlanMode
    {
        get => State.Meta.PlanMode;
        set => State.Meta.PlanMode = value;
    }

    public bool AutoApproveWrites
    {
        get => State.Meta.AutoApproveWrites;
        set => State.Meta.AutoApproveWrites = value;
    }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    /// <summary>
    /// Host-mode toggle requested while a turn was streaming. Applying it
    /// mid-turn would swap the standing-orders block the model is reading or
    /// unpair a gateway the active turn is executing against, so the request
    /// waits and is applied when the turn ends. A second toggle overwrites the
    /// first (latest wins). In-memory only — a queued toggle never survives a
    /// process exit.
    /// </summary>
    public string? PendingHostAction { get; set; }

    private CancellationTokenSource? _turnCts;
    private readonly object _turnCtsGate = new();

    /// <summary>
    /// Registers the <see cref="CancellationTokenSource"/> driving the active turn so
    /// any caller (ACP <c>abort:true</c>, session delete, SSE disconnect) can cancel
    /// it via <see cref="CancelTurn"/>. Reusable turn-wide stop: cancelling flows
    /// through the linked LLM-stream + tool-execution tokens. Call
    /// <see cref="ClearTurn"/> in the turn's finally block.
    /// </summary>
    public void RegisterTurn(CancellationTokenSource cts)
    {
        lock (_turnCtsGate)
        {
            _turnCts?.Dispose();
            _turnCts = cts;
        }
    }

    public void ClearTurn()
    {
        lock (_turnCtsGate)
        {
            _turnCts?.Dispose();
            _turnCts = null;
        }
    }

    /// <summary>
    /// Turn-wide cancel: aborts the in-flight LLM stream + tool calls and drops any
    /// pending permission/user-input pauses. Safe to call when no turn is active.
    /// </summary>
    public void CancelTurn()
    {
        lock (_turnCtsGate)
        {
            try { _turnCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        CancelAllPending();
    }

    private readonly ConcurrentDictionary<string, PendingPause> _pending = new();
    // Single session-scoped permission memory shared by every runtime path.
    // Constructed inline so every session (live, reloaded, test) gets one;
    // grants are in-memory only and never serialized.
    public OpenMono.Permissions.PermissionStore Store { get; } = new();

    // Permission queue: max 1 permission in flight per session
    // Additional permissions queued until current one resolves
    private readonly Queue<(string Id, string ToolName, string Summary, bool Dangerous, bool Elevated, string? Command)> _permissionQueue = new();
    private string? _currentPermissionId;

    public TaskCompletionSource<AcpPauseResponse> RegisterPause(
        string id, PendingResponseKind kind, string contextKey)
    {
        var tcs = new TaskCompletionSource<AcpPauseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pp = new PendingPause(kind, contextKey, tcs);
        if (!_pending.TryAdd(id, pp))
            throw new InvalidOperationException($"Duplicate pause id: {id}");
        return tcs;
    }

    /// <summary>
    /// Long-lived sudo-password pause state: the attempt counter and the
    /// first envelope's consent label persist across re-POSTs against the
    /// SAME still-pending pause (no re-register, no card churn). Cleared when
    /// the pause resolves. Never holds the password.
    /// </summary>
    public (int SudoAttempt, string? Consent)? LookupSudoAttemptState(string id)
        => _pending.TryGetValue(id, out var pp) ? (pp.SudoAttempt, pp.SudoConsent) : null;

    public void RecordSudoAttempt(string id, int attempt, string? consentLabel)
    {
        if (_pending.TryGetValue(id, out var pp))
        {
            pp.SudoAttempt = attempt;
            pp.SudoConsent = consentLabel ?? pp.SudoConsent;
        }
    }

    public bool TryResolvePause(string id, AcpPauseResponse response)
        => _pending.TryRemove(id, out var pp) && pp.Tcs.TrySetResult(response);

    /// <summary>
    /// Mark a permission pause as ELEVATED (root host execution). The resume
    /// branch then requires a password on the permission response, verifies it
    /// against the daemon, and re-enters the same pause on a wrong password
    /// (the pause stays pending across attempts — one card, one pause).
    /// </summary>
    public void MarkPauseElevated(string id)
    {
        if (_pending.TryGetValue(id, out var pp)) pp.Elevated = true;
    }

    public bool IsPauseElevated(string id)
        => _pending.TryGetValue(id, out var pp) && pp.Elevated;

    public (PendingResponseKind Kind, string ContextKey)? LookupPauseContext(string id)
        => _pending.TryGetValue(id, out var pp) ? (pp.Kind, pp.ContextKey) : null;

    /// <summary>
    /// The pending-timeout (session GC) deadline for this pause. Each in-place
    /// sudo-password attempt refreshes it so an actively-typed modal is never
    /// swept; an abandoned modal still expires as before.
    /// </summary>
    public void TouchPauseActivity(string id)
    {
        if (_pending.ContainsKey(id))
            LastActivityAt = DateTime.UtcNow;
    }

    public IReadOnlyCollection<string> PendingIds => _pending.Keys.ToArray();

    public void CancelAllPending()
    {
        foreach (var kv in _pending) kv.Value.Tcs.TrySetCanceled();
        _pending.Clear();
        _currentPermissionId = null;
        _permissionQueue.Clear();
    }

    // Permission queue management (Phase 2 feature)
    public bool TryEnqueuePermission(string id, string toolName, string summary, bool dangerous,
        bool elevated = false, string? command = null)
    {
        // If no permission currently in flight, process immediately
        if (_currentPermissionId == null)
        {
            _currentPermissionId = id;
            return true; // Process immediately
        }

        // Otherwise queue it
        _permissionQueue.Enqueue((id, toolName, summary, dangerous, elevated, command));
        return false; // Queued, don't process yet
    }

    public (string Id, string ToolName, string Summary, bool Dangerous, bool Elevated, string? Command)? DequeueNextPermission()
    {
        _currentPermissionId = null;

        if (_permissionQueue.Count > 0)
        {
            var next = _permissionQueue.Dequeue();
            _currentPermissionId = next.Id;
            return next;
        }

        return null;
    }

    private sealed class PendingPause
    {
        public PendingPause(PendingResponseKind kind, string contextKey, TaskCompletionSource<AcpPauseResponse> tcs)
        {
            Kind = kind;
            ContextKey = contextKey;
            Tcs = tcs;
        }

        public PendingResponseKind Kind;
        public string ContextKey;
        public TaskCompletionSource<AcpPauseResponse> Tcs;
        // Long-lived sudo-password pause: attempt count + first consent label
        // accumulate across re-POSTs while this pause stays unresolved.
        public int SudoAttempt;
        public string? SudoConsent;
        // Elevated (root) permission pause: the permission response must carry
        // a sudo password, verified by the resumer before the pause resolves.
        public bool Elevated;
    }
}

public abstract record AcpPauseResponse;

public sealed record AcpPermissionResponse(bool Allow) : AcpPauseResponse;

public sealed record AcpUserInputResponse(string Value) : AcpPauseResponse;

public sealed record AcpCancelledResponse() : AcpPauseResponse;
