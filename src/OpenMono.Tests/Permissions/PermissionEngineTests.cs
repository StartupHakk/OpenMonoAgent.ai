using System.Text.Json;
using FluentAssertions;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Playbooks;
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


    [Fact]
    public async Task CheckCapabilities_SingleCapability_AllowAll_IsAllowed()
    {
        var input = new ScriptedInputReader(PermissionResponse.AllowAll);
        var engine = new PermissionEngine(new AppConfig(), new TerminalRenderer(), input);

        var result = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/test.txt", "create")], CancellationToken.None);

        result.Allowed.Should().BeTrue("pressing [a] Allow all must permit a single-file write");
    }

    [Fact]
    public async Task CheckCapabilities_SingleCapability_AllowAll_PersistsForSession()
    {
        var input = new ScriptedInputReader(PermissionResponse.AllowAll);
        var engine = new PermissionEngine(new AppConfig(), new TerminalRenderer(), input);

        await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/a.txt", "create")], CancellationToken.None);

        // A second write to a different path must be allowed WITHOUT re-prompting.
        var second = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/b.txt", "modify")], CancellationToken.None);

        second.Allowed.Should().BeTrue("Allow all must stick for the rest of the session");
        input.PromptCount.Should().Be(1, "the second write must not prompt again");
    }

    [Fact]
    public async Task CheckCapabilities_SingleCapability_DenyAll_PersistsForSession()
    {
        var input = new ScriptedInputReader(PermissionResponse.DenyAll);
        var engine = new PermissionEngine(new AppConfig(), new TerminalRenderer(), input);

        var first = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/a.txt", "create")], CancellationToken.None);
        first.Allowed.Should().BeFalse();

        var second = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/b.txt", "modify")], CancellationToken.None);

        second.Allowed.Should().BeFalse("Deny all must stick for the rest of the session");
        input.PromptCount.Should().Be(1, "the second write must not prompt again after Deny all");
    }

    [Fact]
    public async Task CheckCapabilities_SingleCapability_Allow_IsAllowedButReprompts()
    {
        var input = new ScriptedInputReader(PermissionResponse.Allow);
        var engine = new PermissionEngine(new AppConfig(), new TerminalRenderer(), input);

        var first = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/a.txt", "create")], CancellationToken.None);
        var second = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/b.txt", "modify")], CancellationToken.None);

        first.Allowed.Should().BeTrue();
        second.Allowed.Should().BeTrue();
        input.PromptCount.Should().Be(2, "a one-time Allow must prompt again on the next write");
    }

    [Fact]
    public async Task CheckCapabilities_SingleCapability_Deny_IsDeniedAndReprompts()
    {
        var input = new ScriptedInputReader(PermissionResponse.Deny);
        var engine = new PermissionEngine(new AppConfig(), new TerminalRenderer(), input);

        var first = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/a.txt", "create")], CancellationToken.None);
        var second = await engine.CheckCapabilitiesAsync(
            "FileWrite", [new FileWriteCap("/tmp/openmono-test/b.txt", "modify")], CancellationToken.None);

        first.Allowed.Should().BeFalse();
        second.Allowed.Should().BeFalse();
        input.PromptCount.Should().Be(2, "a one-time Deny must prompt again on the next write");
    }

    private static PermissionEngine CreateEngine() =>
        new(new AppConfig(), new TerminalRenderer(), new TerminalRenderer());

    private sealed class ScriptedInputReader : IInputReader
    {
        private readonly PermissionResponse _response;
        public int PromptCount { get; private set; }

        public ScriptedInputReader(PermissionResponse response) => _response = response;

        public Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
        {
            PromptCount++;
            return Task.FromResult(_response);
        }

        public void EnableCommandSuggestions(CommandRegistry registry) { }
        public string ReadInput() => string.Empty;
        public string? ShowCommandPicker(CommandRegistry registry) => null;
        public Task<string> AskUserAsync(string question, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct) => Task.FromResult(false);
    }
}
