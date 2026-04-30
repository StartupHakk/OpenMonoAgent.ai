using FluentAssertions;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void RegisterAndResolve_Works()
    {
        var registry = new ToolRegistry();
        registry.Register(new FileReadTool());

        var tool = registry.Resolve("FileRead");
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("FileRead");
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var registry = new ToolRegistry();
        registry.Register(new FileReadTool());

        registry.Resolve("fileread").Should().NotBeNull();
        registry.Resolve("FILEREAD").Should().NotBeNull();
    }

    [Fact]
    public void Resolve_UnknownTool_ReturnsNull()
    {
        var registry = new ToolRegistry();
        registry.Resolve("nonexistent").Should().BeNull();
    }

    [Fact]
    public void All_ReturnsRegisteredTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new FileReadTool());
        registry.Register(new FileWriteTool());
        registry.Register(new GlobTool());

        registry.All.Should().HaveCount(3);
    }

    [Fact]
    public void BuildToolDefinitions_ProducesValidJson()
    {
        var registry = new ToolRegistry();
        registry.Register(new FileReadTool());

        var defs = registry.BuildToolDefinitions();
        defs.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        defs.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void BuildToolDefinitions_StableOrdering_BuiltinBeforeMcp()
    {
        var registry = new ToolRegistry();

        registry.Register(new FakeTool("Zebra"));
        registry.Register(new FakeTool("mcp__server__tool2"));
        registry.Register(new FakeTool("Apple"));
        registry.Register(new FakeTool("mcp__server__tool1"));
        registry.Register(new FakeTool("Banana"));

        var defs = registry.BuildToolDefinitions();
        var names = new List<string>();

        foreach (var def in defs.EnumerateArray())
        {
            var name = def.GetProperty("function").GetProperty("name").GetString();
            names.Add(name!);
        }

        names[0].Should().Be("Apple");
        names[1].Should().Be("Banana");
        names[2].Should().Be("Zebra");

        names[3].Should().Be("mcp__server__tool1");
        names[4].Should().Be("mcp__server__tool2");
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;
        public string Description => $"Fake {name} tool";
        public System.Text.Json.JsonElement InputSchema =>
            System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;

        public PermissionLevel RequiredPermission(System.Text.Json.JsonElement input) =>
            PermissionLevel.AutoAllow;

        public Task<ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement input,
            ToolContext context,
            CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("ok"));
    }
}
