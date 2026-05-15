using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Acp;

public sealed class AcpSession
{
    public required string Id { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime LastActivityAt { get; set; }
    public required string Model { get; init; }
    public IReadOnlyList<string> ClientTools { get; init; } = Array.Empty<string>();
    public int TurnCount { get; set; }
    public bool PlanMode { get; set; }
    public List<TodoItem> Todos { get; init; } = new();
    public List<Message> Messages { get; init; } = new();

    [JsonIgnore]
    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    // Tool calls awaiting a tool_result from the client. Keyed by call id.
    // Runtime-only: pending requests cannot survive a TUI restart.
    [JsonIgnore]
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolResult>> _pending = new();

    public TaskCompletionSource<ToolResult> RegisterPendingCall(ToolCall call)
    {
        var tcs = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(call.Id, tcs))
            throw new InvalidOperationException($"Duplicate pending tool call id: {call.Id}");
        return tcs;
    }

    public bool TryResolvePendingCall(string callId, ToolResult result)
        => _pending.TryRemove(callId, out var tcs) && tcs.TrySetResult(result);

    [JsonIgnore]
    public IReadOnlyCollection<string> PendingCallIds => _pending.Keys.ToArray();

    public void CancelAllPending()
    {
        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        _pending.Clear();
    }
}
