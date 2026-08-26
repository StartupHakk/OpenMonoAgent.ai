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

    public async Task<ToolResult[]> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        CancellationToken ct)
    {
        if (toolCalls.Count == 0)
            return [];

        Log.Info($"[OMA_DISPATCH] ExecuteToolCallsAsync called with {toolCalls.Count} tool(s): {string.Join(", ", toolCalls.Select(tc => tc.Name))}");

        if (_doomLoop.Check(toolCalls))
        {
            var tier = DoomLoop.RecordHit(DoomLoopDetector.SignatureFor(toolCalls));
            var names = string.Join(", ", toolCalls.Select(tc => tc.Name).Distinct());

            switch (tier)
            {
                case DoomLoopTier.Nudge:
                    _renderer.WriteWarning("Doom loop detected — same tool calls repeated; nudging the agent to vary its approach");
                    return [ToolResult.InvalidInput(
                        $"[System: Doom loop (1st) — you called {names} again with identical arguments. The previous attempt did not make progress. Do NOT repeat the exact same call: inspect the earlier output and take a structurally different step (change an argument, use a different tool, or gather more information first).]",
                        "Vary the call: change an argument, try a different tool, or inspect prior output before proceeding.")];

                case DoomLoopTier.StrongNudge:
                    _renderer.WriteWarning("Doom loop detected — same tool calls repeated 3+ times; escalating the nudge");
                    return [ToolResult.InvalidInput(
                        $"[System: Doom loop (escalated) — {names} has been repeated with identical arguments. Repeating it will not help and the turn will be aborted if it continues. You MUST stop calling {names}. Either: (a) fix the underlying problem first (check file contents / command output / errors already shown), (b) use a different tool or a different set of arguments, or (c) stop and explain to the user what is blocking you and what you need.]",
                        "Stop repeating. Change your approach structurally, or ask the user for help.")];

                default: // DoomLoopTier.Escalate
                    _renderer.WriteWarning("Doom loop detected — same tool calls repeated 5+ times; escalating to the user and ending the turn");
                    return [ToolResult.InvalidInput(
                        $"[System: Doom loop (max) — {names} has been repeated too many times with identical arguments. The turn is being ended and escalated to the user. Do not attempt further tool calls in this step.]",
                        "Escalated to the user — the step will be re-run or the user will be asked for direction.").WithEscalation()];
            }
        }

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
}
