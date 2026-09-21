using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Playbooks;

public sealed class PlaybookState
{
    public required string PlaybookName { get; init; }
    public required string SessionId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> Parameters { get; init; } = [];
    public Dictionary<string, string> StepOutputs { get; init; } = [];
    public List<string> CompletedSteps { get; init; } = [];
    public string? CurrentStepId { get; set; }
    public int TokensUsed { get; set; }

    /// <summary>Doom-loop abort attempts the executor's internal retry loop recovered from.
    /// One entry per aborted attempt that was followed by a re-run (the final hard abort is
    /// NOT recorded here — it travels in the tool result). Serialized snake_case
    /// (<c>aborts: [{step, pattern, attempt, max_attempts, at}]</c>) so harnesses polling the
    /// state file can forward each attempt mid-run. Survives re-runs: the executor carries
    /// this list into each fresh attempt state.</summary>
    public List<PlaybookAbortRecord> Aborts { get; init; } = [];

    public bool IsStepCompleted(string stepId) => CompletedSteps.Contains(stepId);

    public void CompleteStep(string stepId, string output, string? outputKey = null)
    {
        CompletedSteps.Add(stepId);
        StepOutputs[stepId] = output;
        if (!string.IsNullOrEmpty(outputKey) && outputKey != stepId)
            StepOutputs[outputKey] = output;
        CurrentStepId = null;
    }

    public async Task SaveAsync(string dataDirectory, CancellationToken ct)
    {
        var dir = Path.Combine(dataDirectory, "playbook-state");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{PlaybookName}_{SessionId}.json");
        var json = JsonSerializer.Serialize(this, JsonOptions.Indented);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public static async Task<PlaybookState?> LoadAsync(
        string dataDirectory, string playbookName, string sessionId, CancellationToken ct)
    {
        var path = Path.Combine(dataDirectory, "playbook-state", $"{playbookName}_{sessionId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<PlaybookState>(json, JsonOptions.Default);
    }
}

/// <summary>One abort attempt, recorded when the executor's internal retry loop
/// re-runs the playbook. Attempt counts from 1; MaxAttempts is the playbook's
/// retry-attempt-limit. At is a UTC ISO-8601 timestamp. Code is the abort's machine-readable
/// error code (see PlaybookAbortCodes) so harnesses can tell a retried doom loop from a
/// retried tool-loop exhaustion without parsing prose; absent on state written before the
/// field existed.</summary>
public sealed record PlaybookAbortRecord(
    string? Step,
    string? Pattern,
    int Attempt,
    int MaxAttempts,
    string? At,
    string? Code = null);
