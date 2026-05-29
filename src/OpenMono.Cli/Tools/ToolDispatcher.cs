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

    private readonly DoomLoopDetector _doomLoop = new();

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
        IToolExecutor? executor = null)
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

        if (_doomLoop.Check(toolCalls))
        {
            _renderer.WriteWarning("Doom loop detected — same tool calls repeated 3 times");
            return [ToolResult.InvalidInput(
                "[System: Doom loop detected — you called the same tools 3 times in a row with identical arguments. Stop repeating and try a different approach, or ask the user for help.]",
                "Try a different approach or ask the user for clarification.")];
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
            var tasks = parallelItems.Select(async item =>
            {
                try
                {
                    results[item.Index] = await _executor.ExecuteAsync(item.Call, item.Tool, context, ct);
                }
                catch (Exception ex)
                {
                    results[item.Index] = ToolResult.Crash($"Tool crashed: {ex.Message}", "Report this as a bug.");
                }
            });
            await Task.WhenAll(tasks);
        }

        foreach (var item in sequentialItems)
        {
            try
            {
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
