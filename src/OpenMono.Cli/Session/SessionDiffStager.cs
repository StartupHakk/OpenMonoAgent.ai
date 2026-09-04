using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMono.Utils;

namespace OpenMono.Session;

/// <summary>
/// Persists per-session file diffs (before/after content + a linear undo/redo
/// history) under <DataDirectory>/sessions/<sessionId>/diffs/ — the same data
/// directory the session transcript (.jsonl) lives in, so diffs travel with the
/// session and are owned by the agent (in-container) rather than the extension host.
///
/// The extension fetches before/after over the ACP HTTP relay and renders the diff
/// in an in-memory provider; it never opens file:// paths into the user's repo.
///
/// Layout:
///   <sessions>/<id>/diffs/
///     file_<toolId>.before<ext>   // original project file content ("" for new files)
///     file_<toolId>.after<ext>    // new content the agent applied
///     history.json                // ordered change records (for undo/redo + listing)
/// </summary>
public sealed class SessionDiffStager
{
    private readonly string _diffsDir;
    private readonly string _historyPath;
    private readonly object _lock = new();
    private List<DiffRecord> _records;

    public SessionDiffStager(string dataDirectory, string sessionId)
    {
        _diffsDir = Path.Combine(dataDirectory, "sessions", sessionId, "diffs");
        _historyPath = Path.Combine(_diffsDir, "history.json");
        Directory.CreateDirectory(_diffsDir);
        _records = LoadHistory();
    }

    /// <summary>
    /// Record a file change. Call this AFTER the write has succeeded so the
    /// before/after pair matches what is actually on disk.
    /// </summary>
    public void Record(string toolCallId, string filePath, string? contentBefore, string contentAfter)
    {
        var beforeFile = Path.Combine(_diffsDir, SafeName(toolCallId) + ".before" + Path.GetExtension(filePath));
        var afterFile = Path.Combine(_diffsDir, SafeName(toolCallId) + ".after" + Path.GetExtension(filePath));

        var record = new DiffRecord
        {
            ToolCallId = toolCallId,
            FilePath = filePath,
            IsCreation = contentBefore is null,
            Timestamp = DateTime.UtcNow,
            Status = "applied",
            BeforeFile = Path.GetFileName(beforeFile),
            AfterFile = Path.GetFileName(afterFile),
        };

        lock (_lock)
        {
            File.WriteAllText(beforeFile, contentBefore ?? "");
            File.WriteAllText(afterFile, contentAfter ?? "");
            _records.Add(record);
            SaveHistory();
        }

        var kind = record.IsCreation ? "creation" : "change";
        Log.Debug($"[OMA_DIFF] staged {kind} {filePath} tool={toolCallId}");
    }

    public IReadOnlyList<DiffRecord> List()
    {
        lock (_lock)
            return _records.ToList();
    }

    public DiffRecord? Get(string toolCallId)
    {
        lock (_lock)
            return _records.FirstOrDefault(r => r.ToolCallId == toolCallId);
    }

    /// <summary>Read the staged before/after content for a tool call (for the diff editor).</summary>
    public (string? Before, string? After)? GetContent(string toolCallId)
    {
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.ToolCallId == toolCallId);
            if (r is null) return null;
            var b = Path.Combine(_diffsDir, r.BeforeFile);
            var a = Path.Combine(_diffsDir, r.AfterFile);
            return (File.Exists(b) ? File.ReadAllText(b) : null,
                    File.Exists(a) ? File.ReadAllText(a) : null);
        }
    }

    /// <summary>
    /// Undo the most recent applied change: restore the before content (or delete
    /// the file if it was a creation). Returns the affected file path, or null if
    /// there is nothing to undo.
    /// </summary>
    public string? Undo()
    {
        lock (_lock)
        {
            for (var i = _records.Count - 1; i >= 0; i--)
            {
                if (_records[i].Status != "applied") continue;
                var r = _records[i];
                ApplyReverse(r);
                r.Status = "undone";
                SaveHistory();
                return r.FilePath;
            }
            return null;
        }
    }

    /// <summary>Redo the most recently undone change: re-apply the after content.</summary>
    public string? Redo()
    {
        lock (_lock)
        {
            for (var i = _records.Count - 1; i >= 0; i--)
            {
                if (_records[i].Status != "undone") continue;
                var r = _records[i];
                var after = Path.Combine(_diffsDir, r.AfterFile);
                if (File.Exists(after))
                {
                    var dir = Path.GetDirectoryName(r.FilePath);
                    if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(r.FilePath, File.ReadAllText(after));
                }
                r.Status = "applied";
                SaveHistory();
                return r.FilePath;
            }
            return null;
        }
    }

    private void ApplyReverse(DiffRecord r)
    {
        if (r.IsCreation)
        {
            if (File.Exists(r.FilePath)) File.Delete(r.FilePath);
            return;
        }
        var before = Path.Combine(_diffsDir, r.BeforeFile);
        if (File.Exists(before))
        {
            var dir = Path.GetDirectoryName(r.FilePath);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(r.FilePath, File.ReadAllText(before));
        }
    }

    private List<DiffRecord> LoadHistory()
    {
        if (!File.Exists(_historyPath)) return new List<DiffRecord>();
        try
        {
            var json = File.ReadAllText(_historyPath);
            return JsonSerializer.Deserialize<List<DiffRecord>>(json, HistoryJsonOpts) ?? new List<DiffRecord>();
        }
        catch
        {
            return new List<DiffRecord>();
        }
    }

    private void SaveHistory()
    {
        var tmp = _historyPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_records, HistoryJsonOpts));
        File.Move(tmp, _historyPath, overwrite: true);
    }

    private static string SafeName(string id)
    {
        // tool call ids are already url-safe-ish, but guard against path separators
        return string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    }

    private static readonly JsonSerializerOptions HistoryJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public sealed class DiffRecord
{
    [JsonPropertyName("toolCallId")] public string ToolCallId { get; set; } = "";
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
    [JsonPropertyName("isCreation")] public bool IsCreation { get; set; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "applied";
    [JsonPropertyName("beforeFile")] public string BeforeFile { get; set; } = "";
    [JsonPropertyName("afterFile")] public string AfterFile { get; set; } = "";
}
