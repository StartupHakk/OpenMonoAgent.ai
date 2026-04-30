using System.Text.Json;
using FluentAssertions;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class ToolBaseTests
{
    [Fact]
    public void InputSchema_HasCorrectStructure()
    {
        var tool = new SampleTool();

        tool.InputSchema.GetProperty("type").GetString().Should().Be("object");
        tool.InputSchema.GetProperty("properties").GetProperty("file_path")
            .GetProperty("type").GetString().Should().Be("string");
        tool.InputSchema.GetProperty("required").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var tool = new SampleTool();

        tool.IsConcurrencySafe.Should().BeFalse();
        tool.IsReadOnly.Should().BeFalse();
        tool.RequiredPermission(default).Should().Be(PermissionLevel.Ask);
    }

    [Fact]
    public void ReadOnlyTool_DefaultsConcurrencySafe()
    {
        var tool = new ReadOnlySampleTool();

        tool.IsConcurrencySafe.Should().BeTrue();
        tool.IsReadOnly.Should().BeTrue();
        tool.RequiredPermission(default).Should().Be(PermissionLevel.AutoAllow);
    }

    private sealed class SampleTool : ToolBase
    {
        public override string Name => "Sample";
        public override string Description => "A sample tool";

        protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
            .AddString("file_path", "Path to file")
            .Require("file_path");

        protected override Task<ToolResult> ExecuteCoreAsync(
            JsonElement input, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("done"));
    }

    private sealed class ReadOnlySampleTool : ToolBase
    {
        public override string Name => "ReadSample";
        public override string Description => "A read-only sample tool";
        public override bool IsReadOnly => true;
        public override bool IsConcurrencySafe => true;
        public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

        protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
            .AddString("query", "Search query")
            .Require("query");

        protected override Task<ToolResult> ExecuteCoreAsync(
            JsonElement input, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("found"));
    }
}
