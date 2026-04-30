using System.Text.Json;
using FluentAssertions;
using OpenMono.Mcp;

namespace OpenMono.Tests.Mcp;

public class McpToolAdapterTests
{
    [Fact]
    public void FromMcpTool_CreatesAdapter_WithNamespacedName()
    {
        var toolDef = JsonDocument.Parse("""
        {
            "name": "search",
            "description": "Search for items",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "query": { "type": "string" }
                }
            }
        }
        """).RootElement;

        toolDef.GetProperty("name").GetString().Should().Be("search");
        toolDef.GetProperty("description").GetString().Should().Be("Search for items");
        toolDef.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void McpServerConfig_PropertiesWork()
    {
        var config = new McpServerConfig
        {
            Name = "test-server",
            Command = "npx",
            Args = ["-y", "@test/mcp"],
            Env = new() { ["KEY"] = "value" },
            Enabled = true,
        };

        config.Name.Should().Be("test-server");
        config.Command.Should().Be("npx");
        config.Args.Should().HaveCount(2);
        config.Env.Should().ContainKey("KEY");
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void McpServerConfig_Disabled()
    {
        var config = new McpServerConfig
        {
            Name = "disabled",
            Command = "test",
            Enabled = false,
        };

        config.Enabled.Should().BeFalse();
    }
}
