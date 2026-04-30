using System.Text.Json;
using OpenMono.Config;
using OpenMono.History;
using OpenMono.Hooks;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class ToolDispatcher : IDisposable
{
    private readonly ToolRegistry _tools;
    private readonly PermissionEngine _permissions;
    private readonly IRenderer _renderer;
    private readonly AppConfig _config;
    private readonly SessionState _session;
    private readonly HookRunner _hookRunner;
    private readonly TurnJournal _journal;
    private readonly CursorStore _cursorStore;
    private readonly ToolResultCache _cache;
    private readonly ArtifactStore _artifactStore;

    private readonly List<string> _recentToolSignatures = [];
    private const int DoomLoopThreshold = 3;

    public ToolDispatcher(
        ToolRegistry tools,
        PermissionEngine permissions,
        IRenderer renderer,
        AppConfig config,
        SessionState session,
        HookRunner? hookRunner = null,
        TurnJournal? journal = null,
        CursorStore? cursorStore = null,
        ToolResultCache? cache = null,
        ArtifactStore? artifactStore = null)
    {
        _tools = tools;
        _permissions = permissions;
        _renderer = renderer;
        _config = config;
        _session = session;
        _hookRunner = hookRunner ?? new HookRunner(config, msg => _renderer.WriteWarning(msg));
        _journal = journal ?? TurnJournal.ForSession(session, config);
        _cursorStore = cursorStore ?? new CursorStore();
        _cache = cache ?? new ToolResultCache();
        _artifactStore = artifactStore ?? ArtifactStore.ForSession(session, config.DataDirectory);
    }

    public CursorStore Cursors => _cursorStore;

    public ArtifactStore Artifacts => _artifactStore;

    public ToolResultCache Cache => _cache;

    public async Task<ToolResult[]> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        CancellationToken ct)
    {
        if (toolCalls.Count == 0)
            return [];

        var signature = ComputeToolSignature(toolCalls);
        _recentToolSignatures.Add(signature);

        if (DetectDoomLoop(toolCalls))
        {
            _renderer.WriteWarning("Doom loop detected — same tool calls repeated 3 times");
            return [ToolResult.InvalidInput(
                "[System: Doom loop detected — you called the same tools 3 times in a row with identical arguments. Stop repeating and try a different approach, or ask the user for help.]",
                "Try a different approach or ask the user for clarification.")];
        }

        var context = BuildToolContext();
        var results = new ToolResult[toolCalls.Count];

        var readOnlyItems = new List<(ToolCall Call, ITool Tool, int Index)>();
        var writeItems = new List<(ToolCall Call, ITool Tool, int Index)>();

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            var tool = _tools.Resolve(call.Name);

            if (tool is null)
            {
                results[i] = ToolResult.Error($"Unknown tool: {call.Name}");
                continue;
            }

            if (tool.IsReadOnly && tool.IsConcurrencySafe)
                readOnlyItems.Add((call, tool, i));
            else
                writeItems.Add((call, tool, i));
        }

        if (readOnlyItems.Count > 0)
        {
            var tasks = readOnlyItems.Select(async item =>
            {
                try
                {
                    results[item.Index] = await ExecuteSingleToolAsync(item.Call, item.Tool, context, ct);
                }
                catch (Exception ex)
                {
                    results[item.Index] = ToolResult.Crash($"Tool crashed: {ex.Message}", "Report this as a bug.");
                }
            });
            await Task.WhenAll(tasks);
        }

        foreach (var item in writeItems)
        {
            try
            {
                results[item.Index] = await ExecuteSingleToolAsync(item.Call, item.Tool, context, ct);
            }
            catch (Exception ex)
            {
                results[item.Index] = ToolResult.Crash($"Tool crashed: {ex.Message}", "Report this as a bug.");
            }
        }

        return results;
    }

    public async Task<ToolResult> ExecuteSingleToolAsync(
        ToolCall call,
        ITool tool,
        ToolContext context,
        CancellationToken ct)
    {

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
            _renderer.WriteToolDenied(call.Name, validationError);
            Log.Warn($"Tool schema rejected: {call.Name} — {validationError}");
            return ToolResult.Error(validationError);
        }
        _journal.RecordSchemaValidated(call.Id);

        var sanityError = SanityCheck.Check(call.Name, input, _config.WorkingDirectory);
        if (sanityError is not null)
        {
            _journal.RecordSanityRejected(call.Id, sanityError);
            _renderer.WriteToolDenied(call.Name, sanityError);
            Log.Warn($"Tool sanity-rejected: {call.Name} — {sanityError}");
            return ToolResult.Error(sanityError);
        }
        _journal.RecordSanityChecked(call.Id);

        if (_session.Meta.PlanMode && !tool.IsReadOnly)
        {
            var planModeError = $"Plan mode is active — only read-only tools are allowed. " +
                                $"Call ExitPlanMode first to make changes with {call.Name}.";
            _journal.RecordPermissionDecided(call.Id, false, "plan_mode_active");
            _renderer.WriteToolDenied(call.Name, planModeError);
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
            _renderer.WriteToolDenied(call.Name, reason ?? "Permission denied");
            Log.Info($"Tool denied: {call.Name} — {reason ?? "User denied"}");
            return ToolResult.Error(
                $"Permission denied for {call.Name}: {reason ?? "User denied"}. " +
                $"Do not retry this tool call. Ask the user how to proceed instead.");
        }
        _journal.RecordPermissionDecided(call.Id, true);

        if (tool.IsReadOnly && _cache.TryGet(call.Name, input, out var cachedResult) && cachedResult is not null)
        {
            _journal.RecordToolStarted(call.Id);
            _journal.RecordToolCompleted(call.Id, cachedResult.Class, cachedResult.Artifacts.Select(a => a.Id).ToList());
            _renderer.WriteToolStart(call.Name, call.Arguments);
            _renderer.WriteToolSuccess(call.Name);
            Log.Debug($"Tool cache hit: {call.Name}");
            return cachedResult with { ModelPreview = $"[cached] {cachedResult.ModelPreview}" };
        }

        _renderer.WriteToolStart(call.Name, call.Arguments);

        _session.Meta.TokenTracker?.RecordToolUse(call.Name);

        _journal.RecordToolStarted(call.Id);

        try
        {
            await _hookRunner.RunPreToolUseHooksAsync(call.Name, call.Arguments, ct);

            Log.Debug($"Tool executing: {call.Name}");
            var result = await tool.ExecuteAsync(input, context, ct);

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
                _renderer.WriteToolError(call.Name, result.ErrorMessage ?? "Unknown error");
                Log.Warn($"Tool error: {call.Name} — {result.ErrorMessage}");
            }
            else
            {
                _renderer.WriteToolSuccess(call.Name);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _journal.RecordToolCrashed(call.Id, "OperationCanceledException", "cancelled");
            Log.Info($"Tool cancelled: {call.Name}");
            return ToolResult.Cancelled($"{call.Name} was cancelled");
        }
        catch (Exception ex)
        {
            _journal.RecordToolCrashed(call.Id, ex.GetType().Name, ex.Message);
            _renderer.WriteToolError(call.Name, ex.Message);
            Log.Error($"Tool exception: {call.Name}", ex);
            return ToolResult.Crash($"Tool execution failed: {ex.Message}", "Try with different parameters or report this as a bug.");
        }
    }

    public ToolContext BuildToolContext() => new()
    {
        ToolRegistry = _tools,
        Session = _session,
        Permissions = _permissions,
        Config = _config,
        WorkingDirectory = _config.WorkingDirectory,
        WriteOutput = text => _renderer.WriteMarkdown(text),
        AskUser = (question, ct) => _renderer.AskUserAsync(question, ct),
        FileHistory = _session.Meta.FileHistory,
        Cursors = _cursorStore,
    };

    private static string ComputeToolSignature(List<ToolCall> toolCalls)
    {
        var parts = toolCalls.Select(tc => $"{tc.Name}:{tc.Arguments}");
        return string.Join("|", parts);
    }

    private bool DetectDoomLoop(List<ToolCall> currentCalls)
    {
        var currentSig = ComputeToolSignature(currentCalls);
        _recentToolSignatures.Add(currentSig);

        if (_recentToolSignatures.Count < DoomLoopThreshold)
            return false;

        var recent = _recentToolSignatures.TakeLast(DoomLoopThreshold).ToList();
        var isDoomLoop = recent.All(s => s == recent[0]);

        if (_recentToolSignatures.Count > DoomLoopThreshold * 2)
            _recentToolSignatures.RemoveRange(0, _recentToolSignatures.Count - DoomLoopThreshold);

        return isDoomLoop;
    }

    public void Dispose()
    {
        _journal.Dispose();
        _cache.Dispose();
        _artifactStore.Dispose();
    }
}
