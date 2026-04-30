using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Tools;

namespace OpenMono.Tests.Permissions;

public class PermissionEngineTests
{
    [Fact]
    public async Task AutoAllow_AlwaysAllowed()
    {
        var engine = CreateEngine();
        var input = JsonDocument.Parse("{}").RootElement;

        var result = await engine.CheckAsync("FileRead", input, PermissionLevel.AutoAllow, CancellationToken.None);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Deny_AlwaysDenied()
    {
        var engine = CreateEngine();
        var input = JsonDocument.Parse("{}").RootElement;

        var result = await engine.CheckAsync("Dangerous", input, PermissionLevel.Deny, CancellationToken.None);
        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("not permitted");
    }

    [Fact]
    public async Task ConfigAllow_MatchesPattern()
    {
        var config = new AppConfig();

        config.Permissions.Tools["Bash"] = new ToolPermissionRules
        {
            Allow = ["*git*"],
            Deny = [],
            Ask = [],
        };

        var engine = new PermissionEngine(config, new TerminalRenderer(), new TerminalRenderer());
        var input = JsonDocument.Parse("""{"command": "git status"}""").RootElement;

        var result = await engine.CheckAsync("Bash", input, PermissionLevel.Ask, CancellationToken.None);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigDeny_OverridesAllow()
    {
        var config = new AppConfig();

        config.Permissions.Tools["Bash"] = new ToolPermissionRules
        {
            Allow = ["*"],
            Deny = ["*rm -rf*"],
            Ask = [],
        };

        var engine = new PermissionEngine(config, new TerminalRenderer(), new TerminalRenderer());
        var input = JsonDocument.Parse("""{"command": "rm -rf /"}""").RootElement;

        var result = await engine.CheckAsync("Bash", input, PermissionLevel.Ask, CancellationToken.None);
        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ConfigDeny_BlocksAutoAllowTools()
    {
        var config = new AppConfig();

        config.Permissions.Tools["FileRead"] = new ToolPermissionRules
        {
            Allow = [],
            Deny = ["*/etc/shadow*"],
            Ask = [],
        };

        var engine = new PermissionEngine(config, new TerminalRenderer(), new TerminalRenderer());
        var input = JsonDocument.Parse("""{"file_path": "/etc/shadow"}""").RootElement;

        var result = await engine.CheckAsync("FileRead", input, PermissionLevel.AutoAllow, CancellationToken.None);
        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("Denied by permission rule");
    }

    private static PermissionEngine CreateEngine() =>
        new(new AppConfig(), new TerminalRenderer(), new TerminalRenderer());
}
