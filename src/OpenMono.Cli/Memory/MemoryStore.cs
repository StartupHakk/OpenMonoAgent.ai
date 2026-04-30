using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenMono.Memory;

public sealed record MemoryEntry
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Type { get; init; }
    public required string Content { get; init; }
    public required string FilePath { get; init; }
}

public sealed class MemoryStore
{
    private readonly string _memoryDir;
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public MemoryStore(string dataDirectory)
    {
        _memoryDir = Path.Combine(dataDirectory, "memory");
        Directory.CreateDirectory(_memoryDir);
    }

    public string? LoadIndex()
    {
        var indexPath = Path.Combine(_memoryDir, "MEMORY.md");
        return File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
    }

    public IReadOnlyList<MemoryEntry> LoadAll()
    {
        var entries = new List<MemoryEntry>();
        var files = Directory.GetFiles(_memoryDir, "*.md")
            .Where(f => !Path.GetFileName(f).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var entry = ParseMemoryFile(file);
            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    public async Task SaveAsync(string name, string type, string description, string content, CancellationToken ct)
    {
        var fileName = SanitizeFileName(name) + ".md";
        var filePath = Path.Combine(_memoryDir, fileName);

        var fileContent = $"""
            ---
            name: {name}
            description: {description}
            type: {type}
            ---

            {content}
            """;

        await File.WriteAllTextAsync(filePath, fileContent, ct);
        await UpdateIndexAsync(ct);
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        var entries = LoadAll();
        var entry = entries.FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (entry is not null && File.Exists(entry.FilePath))
        {
            File.Delete(entry.FilePath);
            await UpdateIndexAsync(ct);
        }
    }

    private async Task UpdateIndexAsync(CancellationToken ct)
    {
        var entries = LoadAll();
        var lines = entries.Select(e => $"- [{e.Name}]({Path.GetFileName(e.FilePath)}) — {e.Description}");
        var content = string.Join('\n', lines);
        await File.WriteAllTextAsync(
            Path.Combine(_memoryDir, "MEMORY.md"), content, ct);
    }

    private static MemoryEntry? ParseMemoryFile(string path)
    {
        var content = File.ReadAllText(path);
        var parts = content.Split("---", 3, StringSplitOptions.None);

        if (parts.Length < 3) return null;

        try
        {
            var frontmatter = YamlDeserializer.Deserialize<Dictionary<string, string>>(parts[1]);
            return new MemoryEntry
            {
                Name = frontmatter.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(path)),
                Description = frontmatter.GetValueOrDefault("description", ""),
                Type = frontmatter.GetValueOrDefault("type", "project"),
                Content = parts[2].Trim(),
                FilePath = path,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'))
            .ToLowerInvariant();
}
