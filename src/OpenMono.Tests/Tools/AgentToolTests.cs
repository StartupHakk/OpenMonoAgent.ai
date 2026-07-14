using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class AgentToolTests
{
    [Fact]
    public async Task DepthGuard_ReturnsErrorWhenAtMaxNestingDepth()
    {
        var tool = new AgentTool();
        var context = BuildContext(agentDepth: 3);

        var input = JsonDocument.Parse("""
            { "description": "deep task", "prompt": "do something", "agent_type": "Explore" }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("nesting depth limit");
        result.Content.Should().Contain("depth 3");
    }

    [Fact]
    public async Task DepthGuard_ReturnsErrorWhenDeeperThanMax()
    {
        var tool = new AgentTool();
        var context = BuildContext(agentDepth: 5);

        var input = JsonDocument.Parse("""
            { "description": "very deep task", "prompt": "do something", "agent_type": "Explore" }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("nesting depth limit");
    }

    [Fact]
    public async Task UnknownAgentType_ReturnsError()
    {
        var tool = new AgentTool();
        var context = BuildContext(agentDepth: 0);

        var input = JsonDocument.Parse("""
            { "description": "task", "prompt": "do something", "agent_type": "NotARealAgent" }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown agent type: NotARealAgent");
    }

    [Fact]
    public void RequiredCapabilities_ReturnsAgentSpawnCap()
    {
        var tool = new AgentTool();
        var input = JsonDocument.Parse("""
            { "description": "explore code", "prompt": "find stuff", "agent_type": "Explore" }
            """).RootElement;

        var caps = tool.RequiredCapabilities(input);

        caps.Should().HaveCount(1);
        caps[0].Should().BeOfType<AgentSpawnCap>();
        var spawnCap = (AgentSpawnCap)caps[0];
        spawnCap.AgentType.Should().Be("Explore");
        spawnCap.TaskSummary.Should().Be("explore code");
    }

    [Fact]
    public void IsConcurrencySafe_IsTrue()
    {
        new AgentTool().IsConcurrencySafe.Should().BeTrue();
    }

    [Fact]
    public void ToolContext_WithAgentDepth_PreservesAllOtherProperties()
    {
        var original = BuildContext(agentDepth: 0);
        var updated = original.WithAgentDepth(2);

        updated.AgentDepth.Should().Be(2);
        updated.ToolRegistry.Should().BeSameAs(original.ToolRegistry);
        updated.Session.Should().BeSameAs(original.Session);
        updated.Permissions.Should().BeSameAs(original.Permissions);
        updated.Config.Should().BeSameAs(original.Config);
        updated.WorkingDirectory.Should().Be(original.WorkingDirectory);
    }

    [Fact]
    public void ToolContext_DefaultAgentDepth_IsZero()
    {
        var context = BuildContext(agentDepth: 0);
        context.AgentDepth.Should().Be(0);
    }

    [Fact]
    public void AgentConfig_HasSensibleDefaults()
    {
        var config = new AgentConfig();
        config.MaxConcurrentAgents.Should().Be(2);
        config.MaxNestingDepth.Should().Be(3);
    }

    [Fact]
    public void AppConfig_HasAgentSection()
    {
        var config = new AppConfig();
        config.Agents.Should().NotBeNull();
        config.Agents.MaxNestingDepth.Should().Be(3);
    }

    private static ToolContext BuildContext(int agentDepth)
    {
        var config = new AppConfig();
        var renderer = new TerminalRenderer();
        return new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(config, renderer, renderer),
            Config = config,
            WorkingDirectory = config.WorkingDirectory,
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult(""),
            AgentDepth = agentDepth,
        };
    }
}
