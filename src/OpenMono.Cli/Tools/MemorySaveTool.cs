using System.Text.Json;
using OpenMono.Memory;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class MemorySaveTool : ToolBase
{
    public override string Name => "MemorySave";
    public override string Description => "Save a memory that persists across sessions. Use for user preferences, project context, or important decisions.";
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    private readonly MemoryStore _store;

    public MemorySaveTool(MemoryStore store)
    {
        _store = store;
    }

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("name", "Short name for the memory (kebab-case)")
        .AddEnum("type", "Memory type", "user", "feedback", "project", "reference")
        .AddString("description", "One-line description of what this memory contains")
        .AddString("content", "The memory content to persist")
        .Require("name", "type", "description", "content");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var type = input.TryGetProperty("type", out var t) ? t.GetString() : "project";
        return [new MemoryCap(type ?? "project", "write")];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var name = input.GetProperty("name").GetString()!;
        var type = input.GetProperty("type").GetString()!;
        var description = input.GetProperty("description").GetString()!;
        var content = input.GetProperty("content").GetString()!;

        await _store.SaveAsync(name, type, description, content, ct);
        return ToolResult.Success($"Memory saved: {name} ({type})");
    }
}
