using System.Diagnostics;
using System.Text.Json;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Hooks;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class LocalToolExecutor : IToolExecutor
{
    private readonly TurnJournal _journal;
    private readonly IOutputSink _output;
    private readonly AppConfig _config;
    private readonly SessionState _session;
    private readonly PermissionEngine _permissions;
    private readonly ToolResultCache _cache;
    private readonly ArtifactStore _artifactStore;
    private readonly HookRunner _hookRunner;
    private readonly IAcpEventSink? _sink;
    private int _activeToolCount;

    // Throttled live context-usage emission while tools run, so the client's context ring
    // moves during long tool execution instead of freezing until the next API call reports
    // usage. The numerator = last real prompt tokens + estimated growth of tool results
    // already appended to history since that call (chars/4, same estimator as Compactor).
    private static readonly TimeSpan LiveUsageMinInterval = TimeSpan.FromSeconds(2);
    private DateTime _lastLiveUsageUtc = DateTime.MinValue;
    private int _lastEmittedContextTokens;

    public LocalToolExecutor(
        TurnJournal journal,
        IOutputSink output,
        AppConfig config,
        SessionState session,
        PermissionEngine permissions,
        ToolResultCache cache,
        ArtifactStore artifactStore,
        HookRunner hookRunner,
        IAcpEventSink? sink = null)
    {
        _journal = journal;
        _output = output;
        _config = config;
        _session = session;
        _permissions = permissions;
        _cache = cache;
        _artifactStore = artifactStore;
        _hookRunner = hookRunner;
        _sink = sink;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ITool? tool, ToolContext ctx, CancellationToken ct)
    {
        if (tool is null)
            return ToolResult.Error($"Unknown tool: {call.Name}");

        _journal.RecordToolCallReceived(call.Id, call.Name, call.Arguments);

        JsonElement input;
        try
        {
            input = JsonDocument.Parse(call.Arguments).RootElement;
        }
        catch (JsonException ex)
        {
            _journal.RecordSchemaRejected(call.Id, $"json_parse: {ex.Message}");
            return ToolResult.Error(
                $"Invalid JSON arguments for {call.Name}: {ex.Message}\nRaw: {call.Arguments[..Math.Min(200, call.Arguments.Length)]}");
        }

        var validationError = SchemaValidator.Validate(tool.Name, tool.InputSchema, input);
        if (validationError is not null)
        {
            _journal.RecordSchemaRejected(call.Id, validationError);
            _output.WriteToolDenied(call.Name, validationError);
            Log.Warn($"Tool schema rejected: {call.Name} — {validationError}");
            return ToolResult.Error(validationError);
        }
        _journal.RecordSchemaValidated(call.Id);

        var sanityError = SanityCheck.Check(call.Name, input, _config.WorkingDirectory);
        if (sanityError is not null)
        {
            _journal.RecordSanityRejected(call.Id, sanityError);
            _output.WriteToolDenied(call.Name, sanityError);
            Log.Warn($"Tool sanity-rejected: {call.Name} — {sanityError}");
            return ToolResult.Error(sanityError);
        }
        _journal.RecordSanityChecked(call.Id);

        // === SEND TOOL START TO CLIENTS IMMEDIATELY ===
        // A single tool_start is always sent up front so clients can render the proposed
        // change (e.g. a file diff) before the user decides. If the tool needs permission,
        // the PermissionEngine emits a separate `permission_request` that the client
        // correlates back to THIS tool card by its call id. We deliberately do NOT emit a
        // distinct "start with permission" event: it carried an unrelated id from the
        // permission_request and produced a duplicate, non-functional permission card.
        if (_sink is not null)
        {
            Log.Info($"[OMA_TOOLSTART] Sending tool_start: {call.Name}");
            await _sink.OnToolStartAsync(call.Id, call.Name, SummarizeToolArgs(call.Arguments), call.Arguments);
            await EmitLiveContextUsageAsync(force: true);
        }

        // HARD plan-mode gate. Enforced here regardless of the system prompt or tool-def
        // filtering — a weak model can still emit a call for a tool it was never offered.
        // PlanModePolicy is the single allowlist; blocked calls never execute and surface a
        // clean, generic "blocked in plan mode" signal to the UI (start + failed end).
        Log.Info($"[OMA_MODE_GATE] PlanMode={_session.Meta.PlanMode}, Tool={call.Name}, IsReadOnly={tool.IsReadOnly}");
        if (_session.Meta.PlanMode && !PlanModePolicy.IsToolAllowed(tool))
        {
            var planModeError = PlanModePolicy.BlockedMessage(call.Name);
            _journal.RecordPermissionDecided(call.Id, false, "plan_mode_blocked");
            _output.WriteToolDenied(call.Name, planModeError);
            Log.Info($"[OMA_MODE] Tool '{call.Name}' blocked in plan mode (not in read-only allowlist)");
            if (_sink is not null)
            {
                // OnToolStartAsync already called above, just send end
                await _sink.OnToolEndAsync(call.Id, call.Name, ok: false, durationMs: 0.0);
            }
            return ToolResult.Error(planModeError);
        }

        var capabilities = tool.RequiredCapabilities(input);
        bool allowed;
        string? reason;

        Log.Debug($"[EXEC_PERM] {tool.Name}: capabilities.Count={capabilities.Count}, AutoApproveWrites={_session.Meta.AutoApproveWrites}, IsReadOnly={tool.IsReadOnly}");

        if (tool.IsReadOnly)
        {
            // Read-only tools never need user permission, regardless of capabilities
            // They can read but not write, so no user approval needed
            Log.Debug($"[EXEC_PERM] {tool.Name}: AUTO-APPROVED (read-only tool)");
            allowed = true;
            reason = null;
        }
        else if (_session.Meta.AutoApproveWrites)
        {
            // Write tools in auto-approve mode (from approved plan execution)
            Log.Debug($"[EXEC_PERM] {tool.Name}: AUTO-APPROVED (AutoApproveWrites)");
            allowed = true;
            reason = null;
        }
        else if (capabilities.Count > 0)
        {
            // Write tools with required capabilities - ask for permission
            Log.Debug($"[EXEC_PERM] {tool.Name}: checking {capabilities.Count} capabilities");
            var capDecision = await _permissions.CheckCapabilitiesAsync(tool.Name, capabilities, ct);
            allowed = capDecision.Allowed;
            reason = capDecision.Reason;
            Log.Debug($"[EXEC_PERM] {tool.Name}: capability check result: allowed={allowed}");
        }
        else
        {
            // Write tools without capabilities - use legacy permission check
            Log.Debug($"[EXEC_PERM] {tool.Name}: using legacy permission check");
            var permLevel = tool.RequiredPermission(input);
            var legacyDecision = await _permissions.CheckAsync(tool.Name, input, permLevel, ct);
            allowed = legacyDecision.Allowed;
            reason = legacyDecision.Reason;
            Log.Debug($"[EXEC_PERM] {tool.Name}: legacy check result: allowed={allowed}");
        }

        if (!allowed)
        {
            _journal.RecordPermissionDecided(call.Id, false, reason);
            _output.WriteToolDenied(call.Name, reason ?? "Permission denied");
            Log.Info($"Tool denied: {call.Name} — {reason ?? "User denied"}");
            return ToolResult.Error(
                $"Permission denied for {call.Name}: {reason ?? "User denied"}. " +
                $"Do not retry this tool call. Ask the user how to proceed instead.");
        }
        _journal.RecordPermissionDecided(call.Id, true);

        // Send status: processing (tool execution starting now)
        if (_sink is not null)
        {
            await _sink.OnToolStatusAsync(call.Id, "processing");
        }

        if (tool.IsReadOnly && _cache.TryGet(call.Name, input, out var cachedResult) && cachedResult is not null)
        {
            _journal.RecordToolStarted(call.Id);
            _journal.RecordToolCompleted(call.Id, cachedResult.Class, cachedResult.Artifacts.Select(a => a.Id).ToList());
            _output.WriteToolStart(call.Name, call.Arguments);
            _output.WriteToolSuccess(call.Name);
            Log.Debug($"Tool cache hit: {call.Name}");
            if (_sink is not null)
            {
                // OnToolStartAsync already called above, just send end (cache hit means instant execution)
                await _sink.OnToolEndAsync(call.Id, call.Name, ok: true, durationMs: 0.0);
            }
            return cachedResult with { ModelPreview = $"[cached] {cachedResult.ModelPreview}" };
        }

        _output.WriteToolStart(call.Name, call.Arguments);
        _session.Meta.TokenTracker?.RecordToolUse(call.Name);
        _journal.RecordToolStarted(call.Id);

        // The static WriteToolStart line above is a one-shot print — with nothing further until
        // the tool returns, a slow tool (a long Bash command, a sub-agent, a big fetch) leaves the
        // terminal looking frozen. Show a live indicator for the duration of execution instead.
        // ponytail: label reflects whichever concurrent call started the indicator first when
        // several concurrency-safe tools overlap; upgrade to a per-call label if that's confusing.
        if (System.Threading.Interlocked.Increment(ref _activeToolCount) == 1)
            _output.ShowWaitingIndicator($"Running {call.Name}");

        // OnToolStartAsync already called above (before permission check), don't call again
        var stopwatch = Stopwatch.StartNew();

        ToolResult result;

        CancellationTokenSource? timeoutCts = null;
        var execCt = ct;
        if (tool.Timeout is { } toolTimeout && toolTimeout > TimeSpan.Zero)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(toolTimeout);
            execCt = timeoutCts.Token;
        }

        try
        {
            // While the tool runs, keep the client's context ring fresh with a throttled
            // heartbeat (2s) — a single long tool (build, install, sub-agent) would otherwise
            // leave the ring frozen until the tool finishes and the next API call reports usage.
            var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.IsCancellationRequested)
                    {
                        await Task.Delay(LiveUsageMinInterval, heartbeatCts.Token);
                        await EmitLiveContextUsageAsync(force: false);
                    }
                }
                catch (OperationCanceledException) { }
            });

            try
            {
                var hookDecision = await _hookRunner.RunPreToolUseHooksAsync(call.Name, call.Arguments, ct);
                if (!hookDecision.Allowed)
                {
                    var hookReason = hookDecision.Reason ?? "Blocked by a PreToolUse hook";
                    Log.Info($"Tool blocked by PreToolUse hook: {call.Name} — {hookReason}");
                    result = ToolResult.PermissionDenied(
                        $"{call.Name} was blocked by a PreToolUse hook: {hookReason}. " +
                        "Do not retry; address the hook's requirement or ask the user how to proceed.");
                }
                else
                {
                    Log.Debug($"Tool executing: {call.Name} args={call.Arguments}");
                    // Stamp the active tool call id so write tools can stamp per-call
                    // state (diff staging) on the shared session. Writes are sequential in
                    // the ACP path, so this is safe; read-only parallel tools don't read it.
                    _session.CurrentToolCallId = call.Id;
                    try
                    {
                        result = await tool.ExecuteAsync(input, ctx, execCt);
                    }
                    finally
                    {
                        _session.CurrentToolCallId = null;
                    }

                    await _hookRunner.RunPostToolUseHooksAsync(call.Name, result.Content, ct);
                }
            }
            finally
            {
                heartbeatCts.Cancel();
                await heartbeat;
            }

            if (result.Class == ResultClass.Success && result.ModelPreview.Length > _artifactStore.LargeOutputThreshold)
            {
                result = _artifactStore.PersistAndReplace(result, call.Name);
                Log.Debug($"Tool output persisted as artifact: {call.Name}");
            }

            if (tool.IsReadOnly && result.Class == ResultClass.Success)
            {
                _cache.Put(call.Name, input, result);
            }

            if (!tool.IsReadOnly && call.Name is "FileWrite" or "FileEdit" or "ApplyPatch")
            {
                if (input.TryGetProperty("file_path", out var pathEl) && pathEl.GetString() is { } filePath)
                {
                    var resolvedPath = Path.GetFullPath(filePath, _config.WorkingDirectory);
                    _cache.InvalidatePath(resolvedPath);
                    FileReadTool.InvalidateCache(resolvedPath);
                }
            }

            var artifactIds = result.Artifacts.Select(a => a.Id).ToList();
            _journal.RecordToolCompleted(call.Id, result.Class, artifactIds);

            if (result.IsError)
            {
                _output.WriteToolError(call.Name, result.ErrorMessage ?? "Unknown error");
                Log.Warn($"Tool error: {call.Name} — {result.ErrorMessage}");
            }
            else
            {
                _output.WriteToolSuccess(call.Name);


                if (result.Diff is not null)
                    _output.WriteToolDiff(result.Diff);

                if (call.Name is "FileRead" or "FileWrite" &&
                    input.TryGetProperty("file_path", out var fpProp) &&
                    fpProp.GetString() is { } filePath)
                {
                    var content = call.Name == "FileWrite"
                        ? (input.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "")
                        : result.ModelPreview;
                    _output.WriteToolContent(call.Name, filePath, content);
                }
            }
        }
        catch (PendingUserResponseException)
        {
            // User interaction pending (permission, input, etc.) — propagate to turn runner
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            var secs = tool.Timeout!.Value.TotalSeconds;
            _journal.RecordToolCrashed(call.Id, "Timeout", $"timed out after {secs:F0}s");
            _output.WriteToolError(call.Name, $"timed out after {secs:F0}s");
            Log.Warn($"Tool timed out: {call.Name} after {secs:F0}s");
            result = ToolResult.StateConflict(
                $"{call.Name} timed out after {secs:F0}s",
                "Narrow the input (e.g. a more specific path or pattern) and retry.");
        }
        catch (OperationCanceledException)
        {
            _journal.RecordToolCrashed(call.Id, "OperationCanceledException", "cancelled");
            Log.Info($"Tool cancelled: {call.Name}");
            result = ToolResult.Cancelled($"{call.Name} was cancelled");
        }
        catch (Exception ex)
        {
            _journal.RecordToolCrashed(call.Id, ex.GetType().Name, ex.Message);
            _output.WriteToolError(call.Name, ex.Message);
            Log.Error($"Tool exception: {call.Name}", ex);
            result = ToolResult.Crash($"Tool execution failed: {ex.Message}", "Try with different parameters or report this as a bug.");
        }
        finally
        {
            timeoutCts?.Dispose();
            if (System.Threading.Interlocked.Decrement(ref _activeToolCount) == 0)
                _output.ClearWaitingIndicator();
        }

        stopwatch.Stop();
        if (_sink is not null)
        {
            // Send status before tool_end
            await _sink.OnToolStatusAsync(call.Id, result.IsError ? "failed" : "success");
            await _sink.OnToolEndAsync(call.Id, call.Name, ok: !result.IsError, durationMs: stopwatch.Elapsed.TotalMilliseconds);
        }

        await EmitLiveContextUsageAsync(force: true);

        return result;
    }

    /// <summary>
    /// Emit a context-usage update for the client's context ring while tools are running.
    /// <paramref name="force"/> bypasses the throttle (used at each tool boundary); otherwise
    /// emissions are limited to one per <see cref="LiveUsageMinInterval"/> so a burst of fast
    /// tools doesn't spam the SSE stream.
    /// </summary>
    private async Task EmitLiveContextUsageAsync(bool force)
    {
        if (_sink is null) return;
        var tracker = _session.Meta.TokenTracker;
        if (tracker is null || tracker.LastPromptTokens <= 0) return;

        var now = DateTime.UtcNow;
        if (!force && (now - _lastLiveUsageUtc) < LiveUsageMinInterval) return;
        _lastLiveUsageUtc = now;

        var contextTokens = tracker.LastPromptTokens + EstimateNewToolResultTokens();
        if (contextTokens == _lastEmittedContextTokens) return;
        _lastEmittedContextTokens = contextTokens;

        await _sink.OnUsageAsync(
            tracker.TotalPromptTokens,
            tracker.TotalCompletionTokens,
            tracker.TotalTokens,
            contextTokens,
            _config.Llm.ContextSize,
            tracker.LastGenTokensPerSecond,
            tracker.AvgGenTokensPerSecond);
    }

    /// <summary>
    /// Estimate the token growth of tool results appended to history after the last API call
    /// (the last assistant message). Shared chars/4 estimator (see TokenEstimator).
    /// </summary>
    private int EstimateNewToolResultTokens()
    {
        var messages = _session.Messages;
        var lastAssistant = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == MessageRole.Assistant) { lastAssistant = i; break; }
        if (lastAssistant < 0) return 0;

        var chars = 0;
        for (var i = lastAssistant + 1; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role != MessageRole.Tool) continue;
            chars += (m.Content?.Length ?? 0) + 20;
        }
        return TokenEstimator.EstimateForChars(chars);
    }






    internal static string SummarizeToolArgs(string arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return "";
        var trimmed = arguments.AsSpan().Trim();
        if (trimmed.Length == 0) return "";
        var snippet = trimmed.Length <= 120 ? trimmed.ToString() : trimmed[..120].ToString() + "...";

        return string.Join(" ", snippet.Split(new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
