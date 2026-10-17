using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.History;

public sealed class FileHistory
{
    private readonly List<FileSnapshot> _snapshots = [];
    private readonly string _historyDir;

    public IReadOnlyList<FileSnapshot> Snapshots => _snapshots;

    public FileHistory(AppConfig config)
    {
        _historyDir = Path.Combine(config.DataDirectory, "file-history");
        Directory.CreateDirectory(_historyDir);
    }

    public void RecordBefore(string filePath, string toolName, int messageIndex)
    {
        string? contentBefore = null;
        if (File.Exists(filePath))
            contentBefore = File.ReadAllText(filePath);

        _snapshots.Add(new FileSnapshot
        {
            FilePath = filePath,
            ContentBefore = contentBefore,
            ContentAfter = "",
            Timestamp = DateTime.UtcNow,
            ToolName = toolName,
            MessageIndex = messageIndex,
        });
    }

    public void RecordAfter(string filePath)
    {

        for (var i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].FilePath == filePath && _snapshots[i].ContentAfter == "")
            {
                var afterContent = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
                _snapshots[i] = _snapshots[i] with { ContentAfter = afterContent };
                return;
            }
        }
    }

    public async Task<T> TrackAsync<T>(string filePath, string toolName, int messageIndex, Func<Task<T>> action)
    {
        RecordBefore(filePath, toolName, messageIndex);
        var result = await action();
        RecordAfter(filePath);
        return result;
    }

    public async Task<List<string>> RevertAsync(int count, CancellationToken ct)
    {
        var reverted = new List<string>();
        var toRevert = _snapshots.TakeLast(count).Reverse().ToList();

        foreach (var snapshot in toRevert)
        {
            if (snapshot.IsCreation)
            {

                if (File.Exists(snapshot.FilePath))
                {
                    File.Delete(snapshot.FilePath);
                    reverted.Add($"Deleted {snapshot.FilePath} (was created by {snapshot.ToolName})");
                }
            }
            else
            {

                await File.WriteAllTextAsync(snapshot.FilePath, snapshot.ContentBefore!, ct);
                reverted.Add($"Reverted {snapshot.FilePath} (modified by {snapshot.ToolName})");
            }

            _snapshots.Remove(snapshot);
        }

        return reverted;
    }

    public IReadOnlyList<string> GetRecentChanges(int count = 10)
    {
        return _snapshots.TakeLast(count).Reverse().Select(s =>
        {
            var verb = s.IsCreation ? "Created" : "Modified";
            return $"  {s.Timestamp:HH:mm:ss} {verb} {s.FilePath} ({s.ToolName})";
        }).ToList();
    }
}
