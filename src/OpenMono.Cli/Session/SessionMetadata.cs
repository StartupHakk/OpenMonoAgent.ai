using OpenMono.History;

namespace OpenMono.Session;

public sealed class SessionMetadata
{
    public FileHistory? FileHistory { get; set; }
    public TokenTracker? TokenTracker { get; set; }
    public bool PlanMode { get; set; }
    public bool ThinkingEnabled { get; set; }
    public bool DoomLoopDetection { get; set; } = true;
    /// <summary>True while a compaction is actively rewriting session history — drives the "Compacting…" status and ring animation.</summary>
    public bool IsCompacting { get; set; }
    /// <summary>
    /// When true, write/exec tools are auto-approved (no per-edit permission prompt). Set when
    /// the user chooses "Auto implement" for a plan; "Ask before edits" leaves it false so
    /// writes go through the normal permission flow. Honored in LocalToolExecutor.
    /// </summary>
    public bool AutoApproveWrites { get; set; }
    /// <summary>The most recent plan's full text (what the plan says), set by CreatePlan.</summary>
    public string? LastPlanContent { get; set; }
    /// <summary>Workspace-relative path of the most recent plan file written by CreatePlan (where it's saved).</summary>
    public string? LastPlanPath { get; set; }
}
