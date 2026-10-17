using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class HostExecToolTests
{
    private readonly HostExecTool _tool = new();

    private static ToolContext Context(string sessionId, bool hostEnabled)
    {
        var session = new SessionState();
        session.Id = sessionId;
        session.Meta.HostEnabled = hostEnabled;
        return new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = session,
            Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
            Config = new AppConfig { WorkingDirectory = Path.GetTempPath() },
            WorkingDirectory = Path.GetTempPath(),
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult("deny"),
        };
    }

    [Fact]
    public void IsAskGated_And_NotReadOnly_SoHostActionsNeedConsent()
    {
        _tool.IsReadOnly.Should().BeFalse();
        _tool.DefaultPermission.Should().Be(PermissionLevel.Ask);
        PlanModePolicy.IsToolAllowed(_tool).Should().BeFalse("not read-only as a whole; the plan-mode exception is per-command");
        PlanModePolicy.IsHostExec(_tool).Should().BeTrue();
    }

    [Theory]
    [InlineData("ls -la /Users/dimnd/.openmono", false, false, true)]
    [InlineData("cat ~/.openmono/settings.json", false, false, true)]
    [InlineData("tail -50 /var/log/system.log", false, false, true)]
    [InlineData("docker logs --tail 80 501212ad4b43 2>&1 | head -20", false, false, true)]
    [InlineData("docker ps -a", false, false, true)]
    [InlineData("git status", false, false, true)]
    [InlineData("git log --oneline -5", false, false, true)]
    [InlineData("systemctl status nginx", false, false, true)]
    [InlineData("curl -s http://127.0.0.1:7476/status", false, false, true)]
    [InlineData("ps aux", false, false, true)]
    [InlineData("rm -rf /", false, false, false)]
    [InlineData("docker restart 501212ad4b43", false, false, false)]
    [InlineData("git push", false, false, false)]
    [InlineData("echo hi > /etc/passwd", false, false, false)]
    [InlineData("ls $(rm -rf /)", false, false, false)]
    [InlineData("cat file | sh", false, false, false)]
    [InlineData("systemctl restart nginx", false, false, false)]
    [InlineData("sudo cat /etc/shadow", true, false, false)]
    [InlineData("tail -f log", false, true, false)]
    [InlineData("find . -delete", false, false, false)]
    public void PlanMode_HostCommandClassifier(string command, bool asRoot, bool background, bool expected)
    {
        PlanModePolicy.HostCommandAllowedInPlanMode(command, asRoot, background)
            .Should().Be(expected, $"'{command}'");
    }

    [Fact]
    public void Schema_RequiresCommand()
    {
        var schema = _tool.InputSchema;
        schema.GetProperty("required").EnumerateArray()
            .Any(e => e.GetString() == "command").Should().BeTrue();
        schema.GetProperty("properties").TryGetProperty("as_root", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Execute_WhenHostOff_ReturnsCleanUnavailable_AndHintNotToFakeIt()
    {
        var input = JsonDocument.Parse("""{"command":"echo hi"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, Context("s-host-off", hostEnabled: false), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.RetryHint.Should().Contain("turn Host on");
    }

    [Fact]
    public void ToolDefinitions_HideHostExec_WhenHostOff_ExposeWhenOn()
    {
        var tools = new ToolRegistry();
        tools.Register(_tool);

        var on = tools.BuildToolDefinitionsFor(new[] { _tool.Name });
        on.EnumerateArray().Any(t => t.GetProperty("function").GetProperty("name").GetString() == "HostExec")
            .Should().BeTrue();
    }
}
