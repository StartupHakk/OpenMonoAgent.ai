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
        long LatencyMs);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string _path = Path.Combine(config.DataDirectory, "decision-audit.jsonl");

    public async Task AppendAsync(Entry entry, CancellationToken ct = default)
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Warn($"Decision audit append failed: {ex.Message}");
        }
    }
}
