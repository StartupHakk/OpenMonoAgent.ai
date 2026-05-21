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

/// <summary>
/// Canonical single-tool execution path. Both <c>ConversationLoop</c> (normal turns,
/// TUI + ACP) and <c>ToolDispatcher</c> (Playbook-driven turns) route through this
/// type so the user-visible behaviour around denial / plan-mode / diff / SSE events
/// is the same regardless of caller.
///
/// Lifecycle of one call:
/// 1. Journal + JSON parse + schema validate + sanity check.
/// 2. Plan-mode gate (writes blocked when plan mode is on).
/// 3. Permission check (capability- or level-based). Honours
///    <see cref="AcpInputReaderAdapter"/> when ACP mode is active.
/// 4. Cache hit short-circuit for read-only tools.
/// 5. Pre-hooks → tool.ExecuteAsync → post-hooks.
/// 6. Result post-processing (artifact persistence, cache put / path invalidate).
/// 7. Renderer output (start / success / error / diff / content) + journal completion.
/// 8. <see cref="IAcpEventSink"/> emit (tool_start / tool_end) when set.
/// </summary>
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

        if (_session.Meta.PlanMode && !tool.IsReadOnly)
        {
            var planModeError = $"Plan mode is active — investigate and write a plan, do not edit files. " +
                                $"Call ExitPlanMode with your completed plan to resume, then retry {call.Name}.";
            _journal.RecordPermissionDecided(call.Id, false, "plan_mode_active");
            _output.WriteToolDenied(call.Name, planModeError);
            return ToolResult.Error(planModeError);
        }

        var capabilities = tool.RequiredCapabilities(input);
        bool allowed;
        string? reason;

        if (capabilities.Count > 0)
        {
            var capDecision = await _permissions.CheckCapabilitiesAsync(tool.Name, capabilities, ct);
            allowed = capDecision.Allowed;
            reason = capDecision.Reason;
        }
        else
        {
            var permLevel = tool.RequiredPermission(input);
            var legacyDecision = await _permissions.CheckAsync(tool.Name, input, permLevel, ct);
            allowed = legacyDecision.Allowed;
            reason = legacyDecision.Reason;
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

        // Cache hit — synthesize the same renderer / journal / sink sequence as a fresh run
        // but with zero duration. Lets the chat panel still show the tool row.
        if (tool.IsReadOnly && _cache.TryGet(call.Name, input, out var cachedResult) && cachedResult is not null)
        {
            _journal.RecordToolStarted(call.Id);
            _journal.RecordToolCompleted(call.Id, cachedResult.Class, cachedResult.Artifacts.Select(a => a.Id).ToList());
            _output.WriteToolStart(call.Name, call.Arguments);
            _output.WriteToolSuccess(call.Name);
            Log.Debug($"Tool cache hit: {call.Name}");
            if (_sink is not null)
            {
                await _sink.OnToolStartAsync(call.Id, call.Name, SummarizeToolArgs(call.Arguments));
                await _sink.OnToolEndAsync(call.Id, call.Name, ok: true, durationMs: 0.0);
            }
            return cachedResult with { ModelPreview = $"[cached] {cachedResult.ModelPreview}" };
        }

        _output.WriteToolStart(call.Name, call.Arguments);
        _session.Meta.TokenTracker?.RecordToolUse(call.Name);
        _journal.RecordToolStarted(call.Id);

        var stopwatch = Stopwatch.StartNew();
        if (_sink is not null)
            await _sink.OnToolStartAsync(call.Id, call.Name, SummarizeToolArgs(call.Arguments));

        ToolResult result;
        try
        {
            await _hookRunner.RunPreToolUseHooksAsync(call.Name, call.Arguments, ct);

            Log.Debug($"Tool executing: {call.Name}");
            result = await tool.ExecuteAsync(input, ctx, ct);

            await _hookRunner.RunPostToolUseHooksAsync(call.Name, result.Content, ct);

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

                // Diff for edit-shaped tools (was missing from this path before consolidation).
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

        stopwatch.Stop();
        if (_sink is not null)
            await _sink.OnToolEndAsync(call.Id, call.Name, ok: !result.IsError, durationMs: stopwatch.Elapsed.TotalMilliseconds);

        return result;
    }

    /// <summary>
    /// One-line summary of a tool call's arguments for the <c>tool_start</c> SSE event.
    /// Aggressive truncation — the full result preview lands separately via
    /// <c>tool_result_preview</c>.
    /// </summary>
    internal static string SummarizeToolArgs(string arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return "";
        var trimmed = arguments.AsSpan().Trim();
        if (trimmed.Length == 0) return "";
        var snippet = trimmed.Length <= 120 ? trimmed.ToString() : trimmed[..120].ToString() + "...";
        // Collapse multi-line JSON so the chat UI doesn't have to.
        return string.Join(" ", snippet.Split(new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
