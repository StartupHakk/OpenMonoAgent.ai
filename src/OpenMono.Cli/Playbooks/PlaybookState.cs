using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Playbooks;

public sealed class PlaybookState
{
    public required string PlaybookName { get; init; }
    public required string SessionId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> Parameters { get; init; } = [];
    public Dictionary<string, string> StepOutputs { get; } = [];
    public List<string> CompletedSteps { get; } = [];
    public string? CurrentStepId { get; set; }
    public int TokensUsed { get; set; }

    public bool IsStepCompleted(string stepId) => CompletedSteps.Contains(stepId);

    public void CompleteStep(string stepId, string output)
    {
        CompletedSteps.Add(stepId);
        StepOutputs[stepId] = output;
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
