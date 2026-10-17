using OpenMono.HostBridge;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Acp;













public sealed class AcpUserInteractionForwarder : IAcpUserInteraction
{
    private readonly AcpSession _session;
    private readonly SseWriter _writer;
    private readonly TimeSpan _timeout;

    public AcpUserInteractionForwarder(AcpSession session, SseWriter writer, TimeSpan timeout)
    {
        _session = session;
        _writer = writer;
        _timeout = timeout;
    }

    public async Task<(bool Allow, string Scope)> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
    {
        var contextKey = PermissionContextKey(toolName, summary);

        // Session memory first: a remembered grant resolves without a new pause.
        if (_session.Store.TryResolve(contextKey) is { } cached)
            return (cached.Allow, cached.Scope.ToWireString());

        // If a pause for this contextKey is already pending (e.g. from a concurrent
        // speculative tool execution), reuse that pause instead of registering a new
        // one. This prevents multiple concurrent calls from each writing their own
        // permission_request SSE event, which would leave stranded pauses that show
        // up as "crashed: Awaiting client Permission response" in the history.
        foreach (var existingId in _session.PendingIds)
        {
            var ctx = _session.LookupPauseContext(existingId);
            if (ctx?.Kind == PendingResponseKind.Permission && ctx?.ContextKey == contextKey)
                throw new PendingUserResponseException(existingId, PendingResponseKind.Permission);
        }

        var id = "perm_" + Guid.NewGuid().ToString("N")[..12];

        // Try to process immediately, or queue if another permission is in flight
        bool shouldProcess = _session.TryEnqueuePermission(id, toolName, summary, dangerous);

        if (shouldProcess)
        {
            // Register pause and emit permission_request
            var tcs = _session.RegisterPause(id, PendingResponseKind.Permission, contextKey);
            await _writer.WriteEventAsync("permission_request", new
            {
                id,
                tool = toolName,
                summary,
                dangerous,
            });

            // Alert the user in case they've switched away from this agent's tab.
            DesktopNotifier.Alert(
                "OpenMono — permission needed",
                $"The agent needs your permission to run {toolName}.");
        }

        throw new PendingUserResponseException(id, PendingResponseKind.Permission);
    }

    public async Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct)
    {
        var contextKey = "playbook:" + plan.PlaybookName;

        // If approval already granted this session (remembered on resume), return it.
        if (_session.Store.TryResolve(contextKey) is { } cachedPlaybook)
        {
            Utils.Log.Info($"[OMA_PLAYBOOK] RequestPlaybookApprovalAsync: using cached approval for {plan.PlaybookName}");
            return cachedPlaybook.Allow;
        }

        var id = "pbk_" + Guid.NewGuid().ToString("N")[..12];
        var tcs = _session.RegisterPause(id, PendingResponseKind.PlaybookApproval, contextKey);
        Utils.Log.Info($"[OMA_PLAYBOOK] RequestPlaybookApprovalAsync: pause registered id={id} playbook={plan.PlaybookName} requiresModeSwitch={plan.RequiresModeSwitch}");

        await _writer.WriteEventAsync("playbook_permission_request", new
        {
            id,
            playbookName = plan.PlaybookName,
            steps = plan.Steps.Select(s => new
            {
                id = s.Id,
                gate = s.Gate.ToString(),
                description = s.Description,
            }),
            tools = plan.Tools.Select(t => new { name = t.Name, isReadOnly = t.IsReadOnly, dangerous = t.Dangerous }),
            requiresModeSwitch = plan.RequiresModeSwitch,
        });

        DesktopNotifier.Alert(
            "OpenMono — approval needed",
            $"The agent needs your approval to run the '{plan.PlaybookName}' playbook.");

        Utils.Log.Info($"[OMA_PLAYBOOK] RequestPlaybookApprovalAsync: awaiting approval id={id}");
        throw new PendingUserResponseException(id, PendingResponseKind.PlaybookApproval);
    }

    public async Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct)
    {
        var contextKey = "toggle_mode:" + reason;

        if (_session.Store.TryResolve(contextKey) is { } cachedToggle)
            return cachedToggle.Allow;

        var id = "mode_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.ToggleMode, contextKey);

        await _writer.WriteEventAsync("toggle_mode_request", new
        {
            id,
            reason,
        });

        throw new PendingUserResponseException(id, PendingResponseKind.ToggleMode);
    }

    public async Task<string?> RequestUserInputAsync(string question, CancellationToken ct)
    {
        // No general caching: every free-form question prompts. (HOST_EXEC
        // authorization caching lives in the bridge's HostExecPermissionStore
        // as grants, never replayed answers.)
        var id = "ask_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.UserInput, question);

        // HOST_EXEC envelope: emit structured fields next to the raw question
        // so the client can render a proper consent card (command, cwd,
        // escalation, timeout) without re-parsing the question text. Additive:
        // old clients keep the {id, question} shape they already handle, and
        // unparseable envelopes are sent exactly as before.
        if (HostExecRequest.LooksLikeHostExec(question) &&
            HostExecRequest.TryParse(question, out var hostExec) && hostExec is not null)
        {
            await _writer.WriteEventAsync("user_input_request", new
            {
                id,
                question,
                kind = "host_exec",
                command = hostExec.Command,
                cwd = HostGatewayBootstrap.ResolveWorkDir(),
                as_root = hostExec.AsRoot,
                timeout_ms = hostExec.TimeoutMs,
                background = hostExec.Background,
            });
        }
        else
        {
            await _writer.WriteEventAsync("user_input_request", new
            {
                id,
                question,
            });
        }

        DesktopNotifier.Alert(
            "OpenMono — input needed",
            "The agent is waiting for your input.");

        throw new PendingUserResponseException(id, PendingResponseKind.UserInput);
    }


    // Cache by tool name only so one Allow covers all invocations of a tool in a
    // session. Caching by {toolName}|{summary} required a separate dialog for every
    // distinct query/URL, producing 7+ prompts for a single web-research turn.
    // Dangerous tools still show a warning in the UI; they just don't re-prompt
    // if the user already approved that tool earlier in the session.
    public static string PermissionContextKey(string toolName, string summary)
        => toolName;
}
