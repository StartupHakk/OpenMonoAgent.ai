using System.Text.Json;
using OpenMono.Config;
using OpenMono.Utils;

namespace OpenMono.Decisions;

public sealed class DecisionAudit(AppConfig config)
{
    public sealed record Entry(
        string Timestamp,
        string SessionId,
        string Task,
        string Summary,
        long LatencyMs,
        string Backend = "local-heuristic",
        string Model = "local-heuristic",
        string Note = "");

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string _path = Path.Combine(config.DataDirectory, "decision-audit.jsonl");

    /// <summary>
    /// Appends one audit line. Returns true on success, false when the
    /// append failed (unwritable directory, IO error). Callers must surface
    /// a false return to their caller (e.g. an <c>audit=failed</c> marker)
    /// — a silent evidence hole is worse than a noisy one. Never throws
    /// for IO problems and never blocks the tool call.
    /// </summary>
    public async Task<bool> AppendAsync(Entry entry, CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(entry, JsonOptions.Default) + "\n";
            await Gate.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(_path, line, ct);
            }
            finally
            {
                Gate.Release();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Warn($"Decision audit append failed: {ex.Message}");
            return false;
        }
    }
}
