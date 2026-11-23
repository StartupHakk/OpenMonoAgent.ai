namespace OpenMono.Session;

public enum PlanStatus
{
    /// <summary>Presented to the user; awaiting a plan_decision.</summary>
    Pending,
    /// <summary>User chose to implement (auto or gated) — the plan is being executed.</summary>
    Implementing,
    /// <summary>User chose to keep planning — refinements will arrive as new plans.</summary>
    KeptPlanning,
    /// <summary>A newer plan replaced this one before any decision was made.</summary>
    Superseded,
    /// <summary>Chosen for implementation; the implementing turn finished cleanly.</summary>
    Completed,
    /// <summary>Implementation was interrupted (process stop/crash) and never finished.</summary>
    Abandoned,
}

/// <summary>
/// One plan presented via CreatePlan, kept for the session's lifetime so UIs can
/// list history and re-open content after the decision card is gone. Persisted
/// inside SessionMetadata (session header).
/// </summary>
public sealed class PlanRecord
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Workspace-relative path of the saved plan file, when writable.</summary>
    public string? Path { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public PlanStatus Status { get; set; } = PlanStatus.Pending;
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>
    /// Short summary of the implementation outcome (the final assistant message
    /// of the implementing turn), set when the plan reaches Completed. UIs show
    /// this appended after the plan content.
    /// </summary>
    public string? ImplementationSummary { get; set; }
}
