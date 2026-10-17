using System.Text.Json;

namespace OpenMono.HostBridge;

/// <summary>
/// Drives one ACP session as an operator: sends turns, renders the SSE stream,
/// and resolves pauses. Permission pauses follow local policy; HOST_EXEC pauses
/// go through the shared <see cref="HostExecOrchestrator"/> (same engine the
/// headless serve relay uses, so host policy/sudo/audit never diverge);
/// everything else (approvals, free-form input) goes to the operator through
/// the UI (full-screen TUI or plain lines). The model loop and tool execution
/// stay in the container — the only thing that ever runs on bare metal is an
/// approved HOST_EXEC command.
/// </summary>
public sealed class Bridge
{
    private readonly AcpClient _acp;
    private readonly BridgeConfig _config;
    private readonly IBridgeUi _ui;
    private readonly bool _nonInteractive;
    private bool _buildMode;
    // Single session-scoped permission memory: tool grants (base) + host-exec
    // and sudo grants (extension). Replaces _sessionAllow/_sessionDeny.
    private readonly HostExecPermissionStore _store = new();
    // In-container AskUser tool calls carrying a HOST_EXEC envelope have their
    // cards swallowed (the bridge renders that action itself). Pairing is by
    // event order — see HostExecSuppressionTracker. Genuine questions render.
    private readonly HostExecSuppressionTracker _suppression = new();
    // Host decision/execution/audit engine, shared verbatim with the serve
    // relay. The Bridge pump stays here; policy + bare-metal execution live here.
    private readonly HostExecOrchestrator _hostExec;
    // Diagnostic-only pause count (no gate, no cap). Reset per turn.
    private int _pauseCount;
    private string _lastPauseKind = "";

    public Bridge(
        AcpClient acp,
        BridgeConfig config,
        HostExecutor executor,
        HostIdentity identity,
        IBridgeUi ui,
        string workDir,
        bool nonInteractive,
        bool buildMode)
    {
        _acp = acp;
        _config = config;
        _ui = ui;
        _nonInteractive = nonInteractive;
        _buildMode = buildMode;
        _hostExec = new HostExecOrchestrator(config, executor, identity, ui, _store, workDir, nonInteractive);
    }

    public async Task<int> RunTurnAsync(string baseUrl, string sessionId, object payload, CancellationToken ct)
    {
        _pauseCount = 0;
        _lastPauseKind = "";
        _suppression.Clear();
        _ui.ResponseBegin();
        try
        {
            return await PumpAsync(baseUrl, sessionId, _acp.PostTurnStreamAsync(baseUrl, sessionId, payload, ct), ct);
        }
        finally
        {
            _ui.ResponseEnd();
        }
    }

    private async Task<int> PumpAsync(
        string baseUrl,
        string sessionId,
        IAsyncEnumerable<AcpEvent> stream,
        CancellationToken ct)
    {
        await foreach (var evt in stream.WithCancellation(ct))
        {
            var code = await HandleEventAsync(baseUrl, sessionId, evt, ct);
            if (code.HasValue)
                return code.Value;
        }
        return 0;
    }

    private void NotePause(string kind)
    {
        // Observability only: never a gate. Long legitimate tasks keep going;
        // a climbing count tells the operator a pause ping-pong may be stuck.
        _pauseCount++;
        _lastPauseKind = kind;
        if (_pauseCount % 25 == 0)
            _ui.Info($"[bridge] {_pauseCount} pauses this turn (no cap — Ctrl-C to abort). Last: {kind}.");
    }

    private async Task<int?> HandleEventAsync(
        string baseUrl, string sessionId, AcpEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case "text_delta":
                _ui.Text(Str(evt.Data, "content"));
                return null;
            case "thinking_delta":
                _ui.Thinking(Str(evt.Data, "content"));
                return null;
            case "tool_start":
            {
                var name = Str(evt.Data, "name");
                var id = Str(evt.Data, "id");
                if (name.Equals("AskUser", StringComparison.OrdinalIgnoreCase) && id.Length > 0)
                {
                    // Candidate HOST_EXEC carrier: hold the card until its
                    // user_input_request classifies it (exact, no text parsing).
                    _suppression.TrackAskUserStart(id);
                    return null;
                }
                _ui.ToolStart(name, Str(evt.Data, "summary"));
                return null;
            }
            case "tool_end":
            {
                var id = Str(evt.Data, "id");
                if (_suppression.ConsumeIfSuppressed(id))
                    return null;
                var ok = evt.Data.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                var reason = Str(evt.Data, "reason");
                _ui.ToolEnd(Str(evt.Data, "name"), ok, reason);
                return null;
            }
            case "usage":
            {
                // The server emits snake_case (context_tokens/context_window);
                // accept camelCase too in case the wire format ever changes.
                _ui.Usage(NumAny(evt.Data, "context_tokens", "contextTokens"), NumAny(evt.Data, "context_window", "contextWindow"));
                return null;
            }
            case "sub_agent_log":
            {
                var line = Str(evt.Data, "line");
                if (line.Length > 0)
                    _ui.Thinking(line.EndsWith('\n') ? line : line + "\n");
                return null;
            }
            case "permission_request":
            {
                NotePause("permission_request");
                var allow = await ResolveToolPermissionAsync(
                    Str(evt.Data, "tool"), Str(evt.Data, "summary"), IsDangerous(evt.Data), ct);
                return await PumpAsync(baseUrl, sessionId,
                    _acp.PostTurnStreamAsync(baseUrl, sessionId,
                        AcpClient.PermissionPayload(Str(evt.Data, "id"), allow, _config.PermissionScope), ct),
                    ct);
            }
            case "user_input_request":
                NotePause("user_input_request");
                return await ResolveUserInputAsync(baseUrl, sessionId, evt, ct);
            case "playbook_permission_request":
            {
                NotePause("playbook_permission_request");
                var id = Str(evt.Data, "id");
                var name = Str(evt.Data, "playbookName");
                var allow = await _ui.PlaybookAsync(
                    name, ParseSteps(evt.Data), ParseTools(evt.Data),
                    evt.Data.TryGetProperty("requiresModeSwitch", out var rmEl) && rmEl.ValueKind == JsonValueKind.True,
                    ct);
                _ui.PlaybookDecision(name, allow);
                return await PumpAsync(baseUrl, sessionId,
                    _acp.PostTurnStreamAsync(baseUrl, sessionId,
                        AcpClient.PlaybookPayload(id, allow), ct),
                    ct);
            }
            case "toggle_mode_request":
            {
                NotePause("toggle_mode_request");
                var id = Str(evt.Data, "id");
                // The agent only ever asks to go build. If we already put the
                // session there, approve without bothering the operator.
                var approve = _buildMode ||
                    await _ui.ConfirmAsync($"Agent requests build mode ({Str(evt.Data, "reason")}). Approve?", ct);
                if (_buildMode)
                    _ui.Info("[bridge] already in build mode — approving the switch.");
                return await PumpAsync(baseUrl, sessionId,
                    _acp.PostTurnStreamAsync(baseUrl, sessionId,
                        AcpClient.ToggleModePayload(id, approve), ct),
                    ct);
            }
            case "mode_changed":
            {
                var mode = Str(evt.Data, "mode");
                _buildMode = !mode.Equals("plan", StringComparison.OrdinalIgnoreCase);
                _ui.ModeNotice(mode);
                return null;
            }
            case "plan_ready":
            {
                var plan = Str(evt.Data, "plan");
                if (plan.Length > 0)
                    _ui.Markdown(plan);
                _ui.Info("[plan] ready — reply with your decision (or use the prompt).");
                return null;
            }
            case "escalated":
                _ui.Error($"\n[bridge] turn escalated ({Str(evt.Data, "kind")}). It needs your direction.");
                return 2;
            case "done":
                return 0;
            case "error":
                _ui.Error($"\n[bridge] turn error: {Str(evt.Data, "message")}");
                return 1;
            default:
                return null;
        }
    }

    private async Task<int> ResolveUserInputAsync(
        string baseUrl, string sessionId, AcpEvent evt, CancellationToken ct)
    {
        var id = Str(evt.Data, "id");
        var question = Str(evt.Data, "question");

        // Pair with the AskUser tool call that produced this prompt (FIFO —
        // prompts arrive in start order). The tracker marks the linked call
        // suppressed when this is a HOST_EXEC carrier. HOST_EXEC executes
        // regardless of linkage (playbook param prompts have no AskUser
        // origin); genuine questions render their deferred card with full text.
        var isHostExec = HostExecRequest.LooksLikeHostExec(question);
        var linkedToolId = _suppression.LinkPrompt(isHostExec);

        if (isHostExec)
        {
            // Host execution + policy + sudo + audit all live in the shared
            // orchestrator (identical to the headless serve relay path).
            string answer;
            if (HostExecRequest.TryParse(question, out var request) && request is not null)
                answer = await _hostExec.RunAsync(request, ct);
            else
                // Malformed envelope (e.g. an 8 KB heredoc the model failed to
                // JSON-escape). Never park the operator in front of raw JSON:
                // audit it and answer the agent with how to re-send, so the
                // turn resumes instead of stalling on PENDING_RESPONSE.
                answer = _hostExec.HandleMalformedEnvelope(question);
            return await PumpAsync(baseUrl, sessionId,
                _acp.PostTurnStreamAsync(baseUrl, sessionId,
                    AcpClient.UserInputPayload(id, answer), ct),
                ct);
        }

        // Genuine question: render the deferred AskUser card (if it had an
        // AskUser origin), then prompt with the full text.
        if (!isHostExec && linkedToolId is not null)
            _ui.ToolStart("AskUser", question);

        if (_nonInteractive)
        {
            _ui.Error($"\n[bridge] input requested but --non-interactive is set: {Truncate(question, 300)}");
            return 2;
        }
        var answerText = await _ui.InputAsync(question, ct);
        return await PumpAsync(baseUrl, sessionId,
            _acp.PostTurnStreamAsync(baseUrl, sessionId,
                AcpClient.UserInputPayload(id, answerText), ct),
            ct);
    }

    /// <summary>
    /// Kept as a thin forwarder so callers/tests referencing the bridge resolve
    /// to the single canonical answer shape (lives in <see cref="HostExecOrchestrator"/>).
    /// </summary>
    public static string MalformedEnvelopeAnswer(string question) =>
        HostExecOrchestrator.MalformedEnvelopeAnswer(question);

    private async Task<bool> ResolveToolPermissionAsync(string tool, string summary, bool dangerous, CancellationToken ct)
    {
        _config.Tools.TryGetValue(tool, out var rule);
        rule = (rule ?? _config.ToolDefault).Trim().ToLowerInvariant();
        var explicitAllow = _config.Tools.ContainsKey(tool) && rule == "allow";
        // Session memory first (full text to the UI — wrap, never truncate).
        if (_store.TryResolve(tool) is { Allow: false })
        {
            _ui.Info($"[bridge] session-deny {tool} ({summary})");
            return false;
        }
        if (_store.TryResolve(tool) is { Allow: true })
        {
            _ui.Info($"[bridge] session-allow {tool} ({summary}) [{_config.PermissionScope}]");
            return true;
        }
        if (dangerous && !explicitAllow)
        {
            _ui.Info($"[bridge] dangerous {tool} ({summary}) — asking operator.");
            var (allow, scope) = await _ui.PermissionAsync(tool, $"Allow dangerous {tool}? {summary}", ct);
            if (scope == "session")
                _store.Remember(tool, allow, OpenMono.Permissions.GrantScope.Session);
            return allow;
        }
        if (rule == "allow")
        {
            _ui.Info($"[bridge] auto-allow {tool} ({summary}) [{_config.PermissionScope}]");
            return true;
        }
        if (rule == "deny")
        {
            _ui.Info($"[bridge] auto-deny {tool} ({summary})");
            return false;
        }
        var (ok, okScope) = await _ui.PermissionAsync(tool, $"Allow {tool}? {summary}", ct);
        if (okScope == "session")
            _store.Remember(tool, ok, OpenMono.Permissions.GrantScope.Session);
        return ok;
    }

    private static List<PlaybookStep> ParseSteps(JsonElement data)
    {
        var steps = new List<PlaybookStep>();
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                steps.Add(new PlaybookStep(Str(el, "id"), Str(el, "gate"), Str(el, "description")));
            }
        }
        return steps;
    }

    private static List<PlaybookTool> ParseTools(JsonElement data)
    {
        var tools = new List<PlaybookTool>();
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                tools.Add(new PlaybookTool(
                    Str(el, "name"),
                    el.TryGetProperty("isReadOnly", out var ro) && ro.ValueKind == JsonValueKind.True,
                    el.TryGetProperty("dangerous", out var dg) && dg.ValueKind == JsonValueKind.True));
            }
        }
        return tools;
    }

    private static bool IsDangerous(JsonElement data) =>
        data.TryGetProperty("dangerous", out var el) && el.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static long Num(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var el) && el.TryGetInt64(out var v)
            ? v
            : 0;

    /// <summary>
    /// First positive value wins: prefers the server's snake_case usage field,
    /// falls back to camelCase. A missing field reads 0 either way.
    /// </summary>
    private static long NumAny(JsonElement data, string first, string second)
    {
        var v = Num(data, first);
        return v > 0 ? v : Num(data, second);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "...";
}
