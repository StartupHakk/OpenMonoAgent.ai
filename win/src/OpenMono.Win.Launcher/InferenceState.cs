using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMono.Win.Launcher;

// What the last `inference up` resolved, persisted so `stop` knows whether
// OpenMono started llama-server (and may stop it), and so the next start can
// tell whether the running server already has the right model and flags.
public sealed record InferenceState(
    string Backend,
    string Endpoint,
    string? ModelAlias,
    string? ModelFile,
    string? Accel,
    string? LlamaBuild,
    int? ServerPid,
    string? ServerFingerprint,
    string? LogFile,
    string? AgentModel,
    int? ContextTokens,
    bool? VisionEnabled,
    bool StartedByUs,
    string UpdatedUtc)
{
    public const string LlamaBackend = "llama";
    public const string RemoteBackend = "remote";
}

public sealed class InferenceStateStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public InferenceStateStore(string homeDir)
    {
        FilePath = Path.Combine(homeDir, ".openmono", "win", "inference.json");
    }

    public string FilePath { get; }

    public InferenceState? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<InferenceState>(File.ReadAllText(FilePath), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(InferenceState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
