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
    private readonly IToolExecutor _executor;
    private readonly int _maxReadOnlyConcurrency;

    private readonly DoomLoopDetector _doomLoop = new();

    /// <summary>Shared per-session tiered escalation state (nudge → strong nudge → escalate).</summary>
    public DoomLoopState DoomLoop { get; } = new();

    /// <summary>Signature of the most recent tool-call batch seen by the doom-loop guard.
    /// Read by the playbook executor when recording an abort so the repeating pattern travels
    /// with the abort record. Empty when no batch has been seen since the last reset.</summary>
    public string LastDoomSignature => _doomLoop.LastSignature;

    /// <summary>Human-readable repeating sequence from the most recent doom-loop detection,
    /// e.g. "Bash(grep…) → Bash(grep…) → FileRead(login.ts) (period 2)". Richer than
    /// <see cref="LastDoomSignature"/> (which is a single normalized batch) — it shows the
    /// calls that actually cycled, so a downstream abort record carries the "why" not just the
    /// "which single call". Empty until a doom loop fires.</summary>
    public string LastDoomPattern { get; private set; } = "";

    /// <summary>
    /// Full clean slate for the doom-loop guard: clears both the signature history and the
    /// tier streak. Call at the start of each playbook run and each playbook step so one
    /// step's tool calls can never combine with another step's to form a phantom cycle.
    /// </summary>
    public void ResetDoomLoop(string? reason = null)
    {
        _doomLoop.Reset();
        DoomLoop.Reset();
        LastDoomPattern = "";
        if (!string.IsNullOrEmpty(reason))
            Log.Info($"[DOOMLOOP] reset ({reason})");
    }

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
        ArtifactStore? artifactStore = null,
        IToolExecutor? executor = null,
        int? maxReadOnlyConcurrency = null)
    {
        _maxReadOnlyConcurrency = maxReadOnlyConcurrency is { } cap && cap > 0
            ? cap
            : Math.Max(1, Environment.ProcessorCount);
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
        _executor = executor ?? new LocalToolExecutor(
            _journal, _renderer, _config, _session, _permissions, _cache, _artifactStore, _hookRunner);
    }

    public CursorStore Cursors => _cursorStore;

    public ArtifactStore Artifacts => _artifactStore;

    public ToolResultCache Cache => _cache;

    /// <summary>Clears a pending escalation barrier. Called only for executor-driven fresh
    /// work: each internal retry attempt, and explicit new invocations (slash command, new
    /// user message). Never called for model-driven continuations — those stay blocked.</summary>
    public void ClearEscalationAck() => _session.Meta.AwaitingEscalationAck = false;

    public async Task<ToolResult[]> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        CancellationToken ct)
    {
        if (toolCalls.Count == 0)
            return [];

        // Mechanical escalation barrier: after a playbook abort the session waits for genuine
        // new direction (new user message / explicit invocation clears the flag). Prose
        // barriers alone are not enough — observed live: the model acknowledged the abort and
        // then hand-rebuilt the aborted work tool-by-tool. Refusing execution cannot be
        // reasoned around. Playbook-internal step execution is unaffected: a fresh playbook
        // run always starts from a cleared flag (see the clears below).
        if (_session.Meta.AwaitingEscalationAck)
        {
            Log.Warn($"[OMA_ESCALATION] Blocking {toolCalls.Count} tool call(s) — session is awaiting escalation acknowledgement");
            _renderer.WriteWarning("Blocked: the aborted playbook's work must not be re-implemented by hand. Waiting for direction.");
            return toolCalls
                .Select(tc => ToolResult.Error(
                    $"Blocked: playbook work is suspended after an abort ({tc.Name} not executed). " +
                    "Do not retry with other tools — report status and wait for the user's direction."))
                .ToArray();
        }

        Log.Info($"[OMA_DISPATCH] ExecuteToolCallsAsync called with {toolCalls.Count} tool(s): {string.Join(", ", toolCalls.Select(tc => tc.Name))}");

        // NOTE: the agent-facing instructions live in DoomLoopState (DoomLoopPrompts.Nudge /
        // Max, selected by tier). The string literals below are only RetryHint tails appended
        // via ContentForModel — they reinforce the instruction but never carry it alone.
        // Keep per-tier wording in DoomLoopState as the single source of truth.
        if (_doomLoop.Check(toolCalls))
        {
            var tier = DoomLoop.RecordHit();
            var names = string.Join(", ", toolCalls.Select(tc => tc.Name).Distinct());
            var pattern = DescribePattern(toolCalls);
            LastDoomPattern = pattern;
            var sig = Truncate(_doomLoop.LastSignature, 200);
            Log.Warn($"[DOOMLOOP] hit={tier} hits={DoomLoop.ConsecutiveHits}/5 period={_doomLoop.LastPeriod} history={_doomLoop.HistoryCount} tools=[{names}] sig={sig}");

            switch (tier)
            {
                case DoomLoopTier.Nudge:
                    _renderer.WriteWarning($"Doom loop detected — {names} repeated (hit {DoomLoop.ConsecutiveHits}/5); nudging the agent to vary its approach. Pattern: {pattern}");
                    return toolCalls
                        .Select(_ => ToolResult.InvalidInput(
                            DoomLoopPrompts.NudgeWithPattern(names, tier, pattern, DoomLoop.ConsecutiveHits),
                            "Vary the call: change an argument, try a different tool, or inspect prior output before proceeding."))
                        .ToArray();

                case DoomLoopTier.StrongNudge:
                    _renderer.WriteWarning($"Doom loop detected — {names} repeated (hit {DoomLoop.ConsecutiveHits}/5); escalating the nudge. Pattern: {pattern}");
                    return toolCalls
                        .Select(_ => ToolResult.InvalidInput(
                            DoomLoopPrompts.NudgeWithPattern(names, tier, pattern, DoomLoop.ConsecutiveHits),
                            "Stop repeating. Explain what you are trying to do and what you learned, then change your approach structurally."))
                        .ToArray();

                default: // DoomLoopTier.Escalate
                    _renderer.WriteWarning($"Doom loop detected — {names} repeated (hit {DoomLoop.ConsecutiveHits}/5); escalating to the user and ending the turn. Pattern: {pattern}");
                    return toolCalls
                        .Select(_ => ToolResult.InvalidInput(
                            DoomLoopPrompts.MaxWithPattern(names, pattern, DoomLoop.ConsecutiveHits),
                            "Escalated to the user — the step will be re-run or the user will be asked for direction.").WithEscalation())
                        .ToArray();
            }
        }

        if (DoomLoop.RecordClean())
            Log.Info($"[DOOMLOOP] streak cleared after {DoomLoopState.CleanBatchesToClear} clean batches");

        var context = BuildToolContext();
        var results = new ToolResult[toolCalls.Count];

        var parallelItems = new List<(ToolCall Call, ITool Tool, int Index)>();
        var sequentialItems = new List<(ToolCall Call, ITool Tool, int Index)>();

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            var tool = _tools.Resolve(call.Name);

            if (tool is null)
            {
                results[i] = ToolResult.Error($"Unknown tool: {call.Name}");
                continue;
            }

            if (tool.IsConcurrencySafe)
                parallelItems.Add((call, tool, i));
            else
                sequentialItems.Add((call, tool, i));
        }

        if (parallelItems.Count > 0)
        {
            using var gate = new SemaphoreSlim(_maxReadOnlyConcurrency);
            var tasks = parallelItems.Select(async item =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    Log.Info($"[OMA_DISPATCH] Executing (read-only, parallel): {item.Tool.Name}");
                    results[item.Index] = await _executor.ExecuteAsync(item.Call, item.Tool, context, ct);
                }
                catch (Exception ex)
                {
                    results[item.Index] = ToolResult.Crash($"Tool crashed: {ex.Message}", "Report this as a bug.");
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        foreach (var item in sequentialItems)
        {
            try
            {
                Log.Info($"[OMA_DISPATCH] Executing (write, sequential): {item.Tool.Name}");
                results[item.Index] = await _executor.ExecuteAsync(item.Call, item.Tool, context, ct);
            }
            catch (Exception ex)
            {
                results[item.Index] = ToolResult.Crash($"Tool crashed: {ex.Message}", "Report this as a bug.");
            }
        }

        return results;
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
        AskUserWithOptions = (question, options, ct) => _renderer.AskUserAsync(question, options, ct),
        FileHistory = _session.Meta.FileHistory,
        Cursors = _cursorStore,
        Output = _renderer,
    };

    public void Dispose()
    {
        _journal.Dispose();
        _cache.Dispose();
        _artifactStore.Dispose();
    }

    /// <summary>
    /// Human-readable summary of the repeating pattern for logs and model nudges, e.g.
    /// "Bash(command='py…') → Bash(command='grep…') (period 2)". Bounded in length.
    /// </summary>
    private string DescribePattern(List<ToolCall> currentBatch)
    {
        var period = _doomLoop.LastPeriod ?? 1;
        var history = _doomLoop.RecentSignatures;
        var windowSize = Math.Min(history.Count, Math.Max(period * 2, 3));
        var window = history.TakeLast(windowSize).Select(s => Truncate(s, 120)).ToList();
        // Current batch is already in history as the last entry; show batch-level tool
        // names too so Bash-with-different-args cycles are distinguishable.
        var batch = string.Join("+", currentBatch.Select(c =>
            $"{c.Name}({Truncate(LocalToolExecutor.SummarizeToolArgs(c.Arguments), 80)})"));
        return $"{string.Join(" → ", window)} (period {period}, current: {batch})";
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        return value[..max] + "…";
    }
}
