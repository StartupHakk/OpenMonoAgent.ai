using OpenMono.History;

namespace OpenMono.Session;

public sealed class SessionMetadata
{
    public FileHistory? FileHistory { get; set; }
    public TokenTracker? TokenTracker { get; set; }
    public bool PlanMode { get; set; }
    /// <summary>
    /// True when this session can act on the whole host (HOST_EXEC via the
    /// host-bridge). Sandbox sessions leave it false (workspace-only tools).
    /// Drives the HOST:ON/OFF status-bar chip (shared renderer, both TUIs)
    /// and the /host command. Set once per session — never toggled mid-turn.
    /// </summary>
    public bool HostEnabled { get; set; }
    public bool ThinkingEnabled { get; set; }
    public string? ThinkingLevel { get; set; }
    /// <summary>True while a compaction is actively rewriting session history — drives the "Compacting…" status and ring animation.</summary>
    public bool IsCompacting { get; set; }
    /// <summary>True while a checkpoint is summarising history — drives the "Checkpointing..." status and ring animation.</summary>
    public bool IsCheckpointing { get; set; }
    /// <summary>
    /// When true, write/exec tools are auto-approved (no per-edit permission prompt). Set when
    /// the user chooses "Auto implement" for a plan; "Ask before edits" leaves it false so
    /// writes go through the normal permission flow. Honored in LocalToolExecutor.
    /// </summary>
    public bool AutoApproveWrites { get; set; }
    /// <summary>
    /// Name of the git auth profile (settings.json "git" section) active for this
    /// session, or null for legacy host pass-through. Layered onto every Bash child
    /// at spawn time by GitAuthApplier.ApplyTo — so it is hot-swappable mid-session
    /// in both the TUI (/git) and ACP (POST /sessions/{id}/git) without a process
    /// restart, and independent per session under multi-session ACP.
    /// </summary>
    public string? GitProfileName { get; set; }
    /// <summary>
    /// True once the user has explicitly chosen a profile for this session — via
    /// <c>/git &lt;name&gt;</c>, <c>/git off</c>, or the ACP POST /git endpoint.
    /// Persisted in the session header so a resumed session keeps the choice
    /// (including an explicit "off", which must NOT silently fall back to
    /// git.default). When false, startup resolution continues:
    /// OPENMONO_GIT_PROFILE → git.default → host pass-through.
    /// </summary>
    public bool GitProfileTouched { get; set; }
    /// <summary>The most recent plan's full text (what the plan says), set by CreatePlan.</summary>
    public string? LastPlanContent { get; set; }
    /// <summary>Workspace-relative path of the most recent plan file written by CreatePlan (where it's saved).</summary>
    public string? LastPlanPath { get; set; }
    /// <summary>Id of the most recent plan (plan_&lt;12hex&gt;); plan_decision is rejected when its id doesn't match.</summary>
    public string? LastPlanId { get; set; }
    /// <summary>
    /// Every plan presented via CreatePlan this session, oldest first. Each keeps its
    /// id, content and status so UIs (extension drawer, TUI /plans) can list history
    /// and re-open a plan after the decision card is gone. Supersede/stamp lives in
    /// CreatePlanTool + AcpTurnRunner.
    /// </summary>
    public List<PlanRecord> Plans { get; set; } = new();
    /// <summary>
    /// True after a playbook abort / doom-loop escalation appended a User-role stop
    /// barrier. Cleared by the next fresh user message (<c>RunTurnAsync</c>), which
    /// counts as acknowledgement. Lets harnesses block auto-continue until the user
    /// has seen the escalation.
    /// </summary>
    public bool AwaitingEscalationAck { get; set; }

    /// <summary>
    /// Monotonic revision of the mutable session settings (thinking, host mode,
    /// auto-approve, plan mode, git profile, backend). Bumped by
    /// <see cref="SessionSettings"/> on every mutation and persisted in the session
    /// header. Used by <see cref="SessionMetaMerger"/> to resolve cross-process
    /// conflicts: the side with the higher revision wins. Zero means "never mutated
    /// through the settings pipeline" (legacy or pre-bump session file).
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Which writer last bumped <see cref="Revision"> — "tui" or "acp".
    /// Informational (shown in diagnostics); merge decisions use Revision alone.</summary>
    public string? LastWriter { get; set; }

    /// <summary>Bump the settings revision. Called by every <see cref="SessionSettings"/>
    /// mutator right before persist so the new value and its revision land in the same write.</summary>
    public void BumpRevision(string writer)
    {
        Revision++;
        LastWriter = writer;
    }
}
