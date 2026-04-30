using System.Collections.Concurrent;

namespace OpenMono.Session;

public sealed class CursorStore
{
    private readonly ConcurrentDictionary<string, CursorEntry> _cursors = new();
    private const int MaxCursorsPerSession = 100;
    private const int MaxCursorAgeMinutes = 30;

    public string Store(string toolName, object data)
    {

        CleanExpired();

        while (_cursors.Count >= MaxCursorsPerSession)
        {
            var oldest = _cursors.OrderBy(c => c.Value.CreatedAt).FirstOrDefault();
            if (oldest.Key is not null)
                _cursors.TryRemove(oldest.Key, out _);
            else
                break;
        }

        var prefix = toolName.ToLowerInvariant()[..Math.Min(4, toolName.Length)];
        var id = $"{prefix}_{Guid.NewGuid():N}"[..12];

        var entry = new CursorEntry(
            Id: id,
            ToolName: toolName,
            Data: data,
            CreatedAt: DateTime.UtcNow,
            ItemCount: CountItems(data));

        _cursors[id] = entry;
        return id;
    }

    public CursorEntry? Get(string cursorId)
    {
        if (!_cursors.TryGetValue(cursorId, out var entry))
            return null;

        if (DateTime.UtcNow - entry.CreatedAt > TimeSpan.FromMinutes(MaxCursorAgeMinutes))
        {
            _cursors.TryRemove(cursorId, out _);
            return null;
        }

        return entry;
    }

    public T? GetTyped<T>(string cursorId) where T : class
    {
        var entry = Get(cursorId);
        return entry?.Data as T;
    }

    public IReadOnlyList<CursorEntry> List()
    {
        CleanExpired();
        return [.. _cursors.Values.OrderByDescending(c => c.CreatedAt)];
    }

    public void Clear() => _cursors.Clear();

    private void CleanExpired()
    {
        var expiry = DateTime.UtcNow - TimeSpan.FromMinutes(MaxCursorAgeMinutes);
        var expired = _cursors.Where(c => c.Value.CreatedAt < expiry).Select(c => c.Key).ToList();
        foreach (var key in expired)
            _cursors.TryRemove(key, out _);
    }

    private static int CountItems(object data) => data switch
    {
        System.Collections.ICollection c => c.Count,
        System.Collections.IEnumerable e => e.Cast<object>().Count(),
        _ => 1
    };
}

public sealed record CursorEntry(
    string Id,
    string ToolName,
    object Data,
    DateTime CreatedAt,
    int ItemCount);
