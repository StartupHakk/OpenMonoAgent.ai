using System.Text;
using System.Text.Json;

namespace OpenMono.Playbooks;

/// <summary>Source of a recorded LLM call inside a playbook run: the step's own tool loop, an
/// output-schema JSON correction, or a worker-initiated repair turn (reserved; playbooks capture
/// the first two today).</summary>
public enum ContextCallSource { Step, JsonCorrection, Repair }

/// <summary>A tool invocation issued by a single LLM request (Bash command, FileRead path, etc.).</summary>
public sealed record ContextToolCall
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
}

/// <summary>One LLM request's actual token usage, as reported by the provider (llama.cpp via
/// OpenAI-compat). Real per-request numbers (not estimates). A request may issue 0..N tool calls,
/// listed in <see cref="Tools"/>.</summary>
public sealed record ContextCall
{
    public string Step { get; init; } = "";
    public int Index { get; init; }
    public ContextCallSource Source { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int CachedTokens { get; init; }
    public List<ContextToolCall> Tools { get; init; } = [];

    public int FreshTokens => Math.Max(0, PromptTokens - CachedTokens);
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>Rolled-up context usage for one playbook step (aggregate of its calls).</summary>
public sealed record ContextStep
{
    public string Step { get; init; } = "";
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int CachedTokens { get; init; }
    public int PeakPrompt { get; init; }
    public int CallCount { get; init; }
    public List<ContextCall> Calls { get; init; } = [];

    public int FreshTokens => Math.Max(0, PromptTokens - CachedTokens);
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>Accumulates actual LLM context usage during a playbook run. When <see cref="Active"/> is
/// true (playbook declares <c>report_ctx: true</c>) each completed run appends one JSON object to
/// <c>.openmono/data/ctx-usage.jsonl</c> (JSONL, append-only) so every run's full history is retained.
/// Not thread-safe; used by a single playbook execution.</summary>
public sealed class ContextUsageRecorder
{
    public const string FileName = "ctx-usage.jsonl";

    private readonly List<ContextCall> _calls = [];
    private readonly List<ContextStep> _steps = [];
    private int _run;
    private int _stepIndex;

    public bool Active { get; }
    public string Playbook { get; }
    public string SessionId { get; }
    public DateTime StartedAt { get; }

    /// <summary>True until the run completes normally. Set false by the executor on the normal-completion
    /// path so aborted runs (doom loop, gate, script failure, exception) are identifiable in the report.</summary>
    public bool Aborted { get; set; } = true;

    public ContextUsageRecorder(string playbook, string sessionId, string dataDirectory, bool active)
    {
        Playbook = playbook;
        SessionId = sessionId;
        Active = active;
        StartedAt = DateTime.UtcNow;
        if (active)
            _run = ComputeNextRun(dataDirectory);
    }

    private static int ComputeNextRun(string dataDirectory)
    {
        var filePath = CtxFilePath(dataDirectory);
        if (!File.Exists(filePath)) return 1;

        var max = 0;
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("run", out var runEl) && runEl.TryGetInt32(out var n) && n > max)
                    max = n;
            }
        }
        catch
        {
            // Best-effort: on any parse failure, fall back to a run of 1.
            return 1;
        }
        return max + 1;
    }

    /// <summary>Starts a new step bucket. Returns false (and records nothing) when not active.</summary>
    public void BeginStep(string stepId) => _stepIndex = 0;

    /// <summary>Records a single LLM call's usage under the current step. No-op when not active.
    /// <paramref name="tools"/> lists the tool invocations that request issued (name + command).</summary>
    public void Record(string stepId, ContextCallSource source, int promptTokens, int completionTokens, int cachedTokens,
        IEnumerable<ContextToolCall>? tools = null)
    {
        if (!Active || promptTokens < 0 || completionTokens < 0) return;
        _calls.Add(new ContextCall
        {
            Step = stepId,
            Index = _stepIndex++,
            Source = source,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CachedTokens = cachedTokens,
            Tools = tools?.ToList() ?? [],
        });
    }

    /// <summary>Closes the current step, rolling up its calls into <see cref="ContextStep"/>.</summary>
    public void CompleteStep(string stepId)
    {
        if (!Active) return;
        var calls = _calls.Where(c => c.Step == stepId).ToList();
        if (calls.Count == 0) return;

        _steps.Add(new ContextStep
        {
            Step = stepId,
            PromptTokens = calls.Sum(c => c.PromptTokens),
            CompletionTokens = calls.Sum(c => c.CompletionTokens),
            CachedTokens = calls.Sum(c => c.CachedTokens),
            PeakPrompt = calls.Count > 0 ? calls.Max(c => c.PromptTokens) : 0,
            CallCount = calls.Count,
            Calls = calls,
        });
    }

    /// <summary>Appends the run's record as one JSON line to ctx-usage.jsonl. No-op when not active.</summary>
    public void Flush(string dataDirectory)
    {
        if (!Active) return;

        var allCalls = _calls.ToList();
        var repairCalls = allCalls.Where(c => c.Source == ContextCallSource.Repair).ToList();
        var stepRecords = _steps.Select(s => new
        {
            s.Step,
            prompt_tokens = s.PromptTokens,
            completion_tokens = s.CompletionTokens,
            cached_tokens = s.CachedTokens,
            peak_prompt = s.PeakPrompt,
            total_tokens = s.TotalTokens,
            fresh_tokens = s.FreshTokens,
            call_count = s.CallCount,
            calls = s.Calls.Select(c => CallRecord(c)).ToList(),
        }).ToList();

        var runRecord = new
        {
            run = _run,
            playbook = Playbook,
            session_id = SessionId,
            started_at = StartedAt.ToString("O"),
            report_ctx = true,
            aborted = Aborted,
            steps = stepRecords,
            repair = repairCalls.Count > 0 ? new
            {
                call_count = repairCalls.Count,
                prompt_tokens = repairCalls.Sum(c => c.PromptTokens),
                completion_tokens = repairCalls.Sum(c => c.CompletionTokens),
                cached_tokens = repairCalls.Sum(c => c.CachedTokens),
                total_tokens = repairCalls.Sum(c => c.TotalTokens),
                fresh_tokens = repairCalls.Sum(c => c.FreshTokens),
            } : null,
            totals = new
            {
                prompt_tokens = allCalls.Sum(c => c.PromptTokens),
                completion_tokens = allCalls.Sum(c => c.CompletionTokens),
                cached_tokens = allCalls.Sum(c => c.CachedTokens),
                peak_prompt = allCalls.Count > 0 ? allCalls.Max(c => c.PromptTokens) : 0,
                total_tokens = allCalls.Sum(c => c.TotalTokens),
                fresh_tokens = allCalls.Sum(c => c.FreshTokens),
                call_count = allCalls.Count,
            },
        };

        try
        {
            var filePath = CtxFilePath(dataDirectory);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(runRecord, JsonOptions);
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Utils.Log.Warn($"Failed to write ctx-usage for playbook '{Playbook}': {ex.Message}");
        }
    }

    // Serialize one recorded request: per-call totals plus the tool invocations it issued.
    private static object CallRecord(ContextCall c) => new
    {
        c.Step,
        c.Index,
        source = c.Source.ToString().ToLowerInvariant(),
        prompt_tokens = c.PromptTokens,
        completion_tokens = c.CompletionTokens,
        cached_tokens = c.CachedTokens,
        total_tokens = c.TotalTokens,
        fresh_tokens = c.FreshTokens,
        tools = c.Tools.Select(t => new { t.Name, t.Command }).ToList(),
    };

    public static string CtxFilePath(string dataDirectory)
        => Path.Combine(dataDirectory, FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
