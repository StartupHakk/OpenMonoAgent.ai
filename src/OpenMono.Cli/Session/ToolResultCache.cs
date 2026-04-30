using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMono.Tools;

namespace OpenMono.Session;

public sealed class ToolResultCache : IDisposable
{

    public const int DefaultMaxEntries = 500;

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private bool _disposed;

    public ToolResultCache(int? maxEntries = null, TimeSpan? ttl = null)
    {
        _maxEntries = maxEntries ?? DefaultMaxEntries;
        _ttl = ttl ?? DefaultTtl;
    }

    public int Count => _cache.Count;

    public bool TryGet(string toolName, JsonElement input, out ToolResult? result)
    {
        result = null;
        var key = BuildCacheKey(toolName, input);

        if (!_cache.TryGetValue(key, out var entry))
            return false;

        if (DateTime.UtcNow - entry.CreatedAt > _ttl)
        {
            _cache.TryRemove(key, out _);
            return false;
        }

        if (!ValidateResourceState(entry))
        {
            _cache.TryRemove(key, out _);
            return false;
        }

        result = entry.Result;
        return true;
    }

    public void Put(string toolName, JsonElement input, ToolResult result)
    {
        if (result.Class != ResultClass.Success)
            return;

        var key = BuildCacheKey(toolName, input);

        var resourceState = CaptureResourceState(toolName, input);

        var entry = new CacheEntry(
            Key: key,
            ToolName: toolName,
            Result: result,
            CreatedAt: DateTime.UtcNow,
            ResourceState: resourceState);

        if (_cache.Count >= _maxEntries)
            EvictOldestEntries(_maxEntries / 4);

        _cache[key] = entry;
    }

    public void InvalidateTool(string toolName)
    {
        var keysToRemove = _cache.Keys.Where(k => k.StartsWith($"{toolName}:")).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    public void InvalidatePath(string path)
    {
        var normalized = Path.GetFullPath(path);
        var keysToRemove = _cache
            .Where(kvp => kvp.Value.ResourceState?.Path?.Equals(normalized, StringComparison.OrdinalIgnoreCase) == true)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    public void Clear() => _cache.Clear();

    public CacheStats GetStats() => new(
        EntryCount: _cache.Count,
        MaxEntries: _maxEntries,
        OldestEntry: _cache.Values.MinBy(e => e.CreatedAt)?.CreatedAt,
        NewestEntry: _cache.Values.MaxBy(e => e.CreatedAt)?.CreatedAt);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();
    }

    private static string BuildCacheKey(string toolName, JsonElement input)
    {

        var normalized = NormalizeJson(input);
        var hash = ComputeHash(normalized);
        return $"{toolName}:{hash}";
    }

    private static string NormalizeJson(JsonElement element)
    {

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteNormalized(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteNormalized(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteNormalized(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static ResourceState? CaptureResourceState(string toolName, JsonElement input)
    {

        if (toolName is "FileRead" or "Grep" or "Glob")
        {
            string? path = null;

            if (input.TryGetProperty("file_path", out var fp))
                path = fp.GetString();
            else if (input.TryGetProperty("path", out var p))
                path = p.GetString();

            if (path is not null && File.Exists(path))
            {
                var info = new FileInfo(path);
                return new ResourceState(
                    Path: Path.GetFullPath(path),
                    MtimeTicks: info.LastWriteTimeUtc.Ticks,
                    SizeBytes: info.Length);
            }
        }

        return null;
    }

    private static bool ValidateResourceState(CacheEntry entry)
    {
        if (entry.ResourceState is null)
            return true;

        var state = entry.ResourceState;
        if (!File.Exists(state.Path))
            return false;

        var current = new FileInfo(state.Path);
        return current.LastWriteTimeUtc.Ticks == state.MtimeTicks &&
               current.Length == state.SizeBytes;
    }

    private void EvictOldestEntries(int count)
    {
        var toEvict = _cache
            .OrderBy(kvp => kvp.Value.CreatedAt)
            .Take(count)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
            _cache.TryRemove(key, out _);
    }

    private sealed record CacheEntry(
        string Key,
        string ToolName,
        ToolResult Result,
        DateTime CreatedAt,
        ResourceState? ResourceState);

    private sealed record ResourceState(
        string Path,
        long MtimeTicks,
        long SizeBytes);
}

public sealed record CacheStats(
    int EntryCount,
    int MaxEntries,
    DateTime? OldestEntry,
    DateTime? NewestEntry);
