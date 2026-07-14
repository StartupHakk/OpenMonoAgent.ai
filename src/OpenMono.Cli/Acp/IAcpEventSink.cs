namespace OpenMono.Acp;






public interface IAcpEventSink
{
    Task OnTextDeltaAsync(string content);
    Task OnThinkingDeltaAsync(string content);


    Task OnToolStartAsync(string callId, string name, string summary, string? arguments = null);

    Task OnToolStatusAsync(string callId, string status);

    Task OnToolEndAsync(string callId, string name, bool ok, double durationMs);

    Task OnToolResultPreviewAsync(string callId, string preview, string? artifactId);

    // Emitted when the session mode changes DURING a turn (e.g. the LLM called
    // EnterPlanMode/ExitPlanMode). Lets the frontend keep its Plan/Build toggle in sync —
    // the agent must never change mode without the UI/TUI learning about it. The backend is
    // the source of truth; this is how it pushes an agent-initiated change to the frontend.
    Task OnModeChangedAsync(string mode);

    // Emitted when the agent has presented a plan (CreatePlan). Carries the plan content so the
    // frontend renders the plan + proceed-options (buttons) as one card; the choice routes back
    // through a plan_decision turn.
    Task OnPlanReadyAsync(string planContent, string? planPath);

    Task OnCompactionAsync(int messagesCompressed, double durationSeconds, int checkpointIndex);
    // contextTokens = prompt tokens of the most recent API call (current context occupancy);
    // contextWindow = the model's n_ctx (denominator for a context-usage gauge).
    // genTps = most recent turn's live generation rate; avgTps = session rolling average (tok/s).
    Task OnUsageAsync(int inputTokens, int outputTokens, int totalTokens, int contextTokens, int contextWindow, double genTps, double avgTps);

    // Progress lines forwarded from sub-agents via WriteOutput/StreamText.
    // These are distinct from the main assistant text stream.
    // Fire-and-forget from synchronous ToolContext delegates; order is best-effort.
    Task OnSubAgentLogAsync(string line);
}
