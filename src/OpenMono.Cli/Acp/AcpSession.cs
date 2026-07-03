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

    private readonly ConcurrentDictionary<string, PendingPause> _pending = new();
    private readonly ConcurrentDictionary<string, bool> _rememberedPermissions = new();
    private readonly ConcurrentDictionary<string, string> _rememberedUserInputs = new();

    public TaskCompletionSource<AcpPauseResponse> RegisterPause(
        string id, PendingResponseKind kind, string contextKey)
    {
        var tcs = new TaskCompletionSource<AcpPauseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new PendingPause(kind, contextKey, tcs)))
            throw new InvalidOperationException($"Duplicate pause id: {id}");
        return tcs;
    }

    public bool TryResolvePause(string id, AcpPauseResponse response)
        => _pending.TryRemove(id, out var pp) && pp.Tcs.TrySetResult(response);

    public (PendingResponseKind Kind, string ContextKey)? LookupPauseContext(string id)
        => _pending.TryGetValue(id, out var pp) ? (pp.Kind, pp.ContextKey) : null;

    public IReadOnlyCollection<string> PendingIds => _pending.Keys.ToArray();

    public void CancelAllPending()
    {
        foreach (var kv in _pending) kv.Value.Tcs.TrySetCanceled();
        _pending.Clear();
    }

    public void RememberPermission(string contextKey, bool allow)
        => _rememberedPermissions[contextKey] = allow;

    public bool? TryGetRememberedPermission(string contextKey)
        => _rememberedPermissions.TryGetValue(contextKey, out var v) ? v : null;

    // Drop a remembered decision. Used for "once" scope: a temporary grant is
    // seeded so the resumed tool execution passes without re-prompting, then
    // forgotten immediately so a later call this session prompts again.
    public void ForgetPermission(string contextKey)
        => _rememberedPermissions.TryRemove(contextKey, out _);

    public void RememberUserInput(string contextKey, string value)
        => _rememberedUserInputs[contextKey] = value;

    public string? TryGetRememberedUserInput(string contextKey)
        => _rememberedUserInputs.TryGetValue(contextKey, out var v) ? v : null;

    private sealed record PendingPause(
        PendingResponseKind Kind,
        string ContextKey,
        TaskCompletionSource<AcpPauseResponse> Tcs);
}

public abstract record AcpPauseResponse;

public sealed record AcpPermissionResponse(bool Allow) : AcpPauseResponse;

public sealed record AcpUserInputResponse(string Value) : AcpPauseResponse;

public sealed record AcpCancelledResponse() : AcpPauseResponse;
