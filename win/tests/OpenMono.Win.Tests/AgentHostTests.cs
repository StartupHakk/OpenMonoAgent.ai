using FluentAssertions;
using Xunit;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class WindowsShellTests
{
    [Fact]
    public void HostShellIsPowerShellOnly()
    {
        // Host controls are PowerShell. The agent keeps bash in its Linux
        // container; cmd.exe and Git Bash are never host shells.
        WindowsShellSelector.PreferredShells().Should().Equal("pwsh.exe", "powershell.exe");
    }

    [Fact]
    public void ResolvesFirstAvailable()
    {
        WindowsShellSelector.ResolveAvailable(name => name == "powershell.exe").Should().Be("powershell.exe");
    }

    [Fact]
    public void ThrowsWhenNothingAvailable()
    {
        var act = () => WindowsShellSelector.ResolveAvailable(_ => false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*pwsh.exe*powershell.exe*");
    }
}

public sealed class WindowsGuardrailTests
{
    [Theory]
    [InlineData("format C:")]
    [InlineData("diskpart")]
    [InlineData("bcdedit /enum")]
    [InlineData("reg delete HKLM\\Software\\Foo /f")]
    [InlineData("Remove-Item C:\\ -Recurse -Force")]
    [InlineData("Remove-Item -Recurse C:\\Windows")]
    [InlineData("rmdir /s C:\\")]
    public void DangerousHostCommandsDenied(string command)
    {
        WindowsGuardrails.IsDenied(command, out var reason).Should().BeTrue();
        reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("pwsh -File bootstrap.ps1")]
    [InlineData("docker compose run -d agent")]
    [InlineData("Remove-Item C:\\Users\\spencer\\temp\\scratch -Recurse")]
    [InlineData("reg query HKLM\\Software\\Foo")]
    [InlineData("foundry server status")]
    public void BenignHostCommandsPass(string command)
    {
        WindowsGuardrails.IsDenied(command, out _).Should().BeFalse();
    }

    [Fact]
    public void DenyPatternsTargetHostShellUse()
    {
        WindowsGuardrails.DenyPatterns("Bash").Should().NotBeEmpty();
        WindowsGuardrails.DenyPatterns("FileRead").Should().BeEmpty();
    }
}

public sealed class WindowsPathMapperTests
{
    [Fact]
    public void ToFileUriFormatsDrivePath()
    {
        WindowsPathMapper.ToFileUri("C:\\Users\\spencer\\proj").Should().Be("file:///C:/Users/spencer/proj");
    }

    [Fact]
    public void ToFileUriEscapesSpaces()
    {
        WindowsPathMapper.ToFileUri("C:\\My Docs\\a.txt").Should().Be("file:///C:/My%20Docs/a.txt");
    }

    [Fact]
    public void RoundTrips()
    {
        var uri = WindowsPathMapper.ToFileUri("C:\\Users\\spencer\\a b.txt");
        var back = WindowsPathMapper.FromFileUri(uri);
        var expected = OperatingSystem.IsWindows() ? "C:\\Users\\spencer\\a b.txt" : "C:/Users/spencer/a b.txt";
        back.Should().Be(expected);
    }

    [Fact]
    public void RejectsNonFileUri()
    {
        var act = () => WindowsPathMapper.FromFileUri("https://example.com/x");
        act.Should().Throw<ArgumentException>();
    }
}

public sealed class WindowsContainerEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:7474", "http://host.docker.internal:7474")]
    [InlineData("http://localhost:7474", "http://host.docker.internal:7474")]
    [InlineData("http://127.0.0.1:7474/v1", "http://host.docker.internal:7474/v1")]
    [InlineData("http://127.0.0.1:7474/", "http://host.docker.internal:7474")]
    public void LoopbackMapsToContainerHostRoute(string input, string expected)
    {
        WindowsContainerEndpoint.MapForContainer(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://192.168.1.10:7474")]
    [InlineData("http://inference.lan:7474/v1")]
    public void RemotePassesThrough(string input)
    {
        WindowsContainerEndpoint.MapForContainer(input).Should().Be(input);
    }
}

public sealed class WindowsContainerStackTests
{
    private const string Work = "C:\\Users\\spencer\\proj";
    private const string Home = "C:\\Users\\spencer";
    private const string Compose = "C:\\openmono\\docker\\docker-compose.yml";

    private static AgentRunRequest Request(params string[] agentArgs) => new(
        WorkDir: Work,
        HomeDir: Home,
        ComposeFile: Compose,
        AgentArgs: agentArgs,
        Endpoint: "http://127.0.0.1:7474",
        Model: "qwen3.8-27b-cuda-gpu:1",
        ContextTokens: 65536,
        VisionEnabled: false,
        FileExists: _ => false,
        DirectoryExists: _ => false);

    [Fact]
    public void ComposeEnvironmentPointsAgentAtHostFoundry()
    {
        var plan = WindowsContainerStack.Build(Request());

        plan.Environment["OPENMONO_ENDPOINT"].Should().Be("http://host.docker.internal:7474");
        plan.Environment["WORKSPACE"].Should().Be("C:/Users/spencer/proj");
        plan.Environment["HOME"].Should().Be("C:/Users/spencer");
        plan.Environment["CTX_SIZE"].Should().Be("65536");
        plan.Environment["OPENMONO_VISION_ENABLED"].Should().Be("0");
    }

    [Fact]
    public void DefaultRunMirrorsBashSandboxLauncher()
    {
        var plan = WindowsContainerStack.Build(Request());

        plan.DockerArgs.Should().StartWith(["compose", "-f", Compose, "run", "--rm", "--user", "1000:1000"]);
        plan.DockerArgs.Should().ContainInOrder("-e", "HOME=/home/agent");
        plan.DockerArgs.Should().Contain($"WORKSPACE={Work}");
        plan.DockerArgs.Should().Contain($"HOST_WORKSPACE_PATH={Work}");
        plan.DockerArgs.Should().Contain("OPENMONO_MODEL=qwen3.8-27b-cuda-gpu:1");
        plan.DockerArgs.Should().Contain("GIT_TERMINAL_PROMPT=0");
        plan.DockerArgs.Should().Contain("GIT_CONFIG_VALUE_0=/workspace");
        plan.DockerArgs[^1].Should().Be("agent");
        plan.DockerArgs.Should().NotContain("-d");
        plan.DockerArgs.Should().NotContain("--name");
        plan.DockerArgs.Should().NotContain("-p");
        plan.AcpHostPort.Should().BeNull();
    }

    [Fact]
    public void AgentArgsPassThroughAfterServiceName()
    {
        var plan = WindowsContainerStack.Build(Request("-p", "explain this repo"));

        plan.DockerArgs.TakeLast(3).Should().Equal("agent", "-p", "explain this repo");
        plan.AcpHostPort.Should().BeNull();
    }

    [Fact]
    public void AcpOnlyPublishesLoopbackPort()
    {
        var plan = WindowsContainerStack.Build(Request("--acp-only"));

        plan.DockerArgs.Should().ContainInOrder("-p", "127.0.0.1:7475:7475");
        plan.DockerArgs.Should().Contain("HOST_ACP_PORT=7475");
        plan.AcpHostPort.Should().Be(7475);
    }

    [Fact]
    public void AcpOnlyHonorsExplicitPort()
    {
        var plan = WindowsContainerStack.Build(Request("--acp-only", "--acp-port", "7575"));

        plan.DockerArgs.Should().ContainInOrder("-p", "127.0.0.1:7575:7575");
        plan.DockerArgs.Should().Contain("HOST_ACP_PORT=7575");
    }

    [Fact]
    public void ApiKeyNeverAppearsInPlan()
    {
        var plan = WindowsContainerStack.Build(Request());

        plan.DockerArgs.Should().NotContain(a => a.Contains("OPENMONO_API_KEY"));
        plan.Environment.Keys.Should().NotContain("OPENMONO_API_KEY");
    }

    [Fact]
    public void GitMountsOnlyWhatExists()
    {
        var request = Request() with
        {
            FileExists = p => p.EndsWith(".gitconfig", StringComparison.Ordinal),
            DirectoryExists = p => p.EndsWith(".ssh", StringComparison.Ordinal),
        };

        var plan = WindowsContainerStack.Build(request);

        plan.DockerArgs.Should().Contain(a => a.EndsWith("/.gitconfig:/home/agent/.gitconfig:ro"));
        plan.DockerArgs.Should().Contain(a => a.EndsWith("/.ssh:/home/agent/.ssh:ro"));
        plan.DockerArgs.Should().NotContain(a => a.Contains(".git-credentials"));
    }

    [Fact]
    public void NoGitAuthSkipsGitPlumbing()
    {
        var plan = WindowsContainerStack.Build(Request() with { NoGitAuth = true });

        plan.DockerArgs.Should().NotContain(a => a.StartsWith("GIT_"));
    }

    [Fact]
    public void RemoteEndpointPassesThroughUntouched()
    {
        var plan = WindowsContainerStack.Build(Request() with { Endpoint = "http://inference.lan:7474" });

        plan.Environment["OPENMONO_ENDPOINT"].Should().Be("http://inference.lan:7474");
    }

    [Fact]
    public void LocalWebGatewayIsDroppedWithNote()
    {
        var plan = WindowsContainerStack.Build(Request() with { WebGateway = "http://localhost:8080", WebSearch = "1" });

        plan.Environment.Keys.Should().NotContain("OPENMONO_WEB_GATEWAY");
        plan.Notes.Should().ContainSingle(n => n.Contains("web.gateway"));
    }

    [Fact]
    public void RemoteWebGatewayPassesThrough()
    {
        var plan = WindowsContainerStack.Build(Request() with { WebGateway = "http://gateway.lan:8080", WebSearch = "1" });

        plan.Environment["OPENMONO_WEB_GATEWAY"].Should().Be("http://gateway.lan:8080");
        plan.Environment["OPENMONO_WEB_SEARCH"].Should().Be("1");
    }
}

public sealed class WindowsAcpWiringTests
{
    [Fact]
    public void PublishArgBindsLoopbackOnly()
    {
        WindowsAcpWiring.PortPublishArg(7475).Should().Be("-p 127.0.0.1:7475:7475");
    }

    [Fact]
    public void DiscoveryUrlIsExtensionTarget()
    {
        WindowsAcpWiring.DiscoveryUrl(7475).Should().Be("http://127.0.0.1:7475/api/v1/discovery");
    }

    [Fact]
    public void LockPathSitsNextToWorkspace()
    {
        var path = WindowsAcpWiring.AgentLockPath(
            OperatingSystem.IsWindows() ? "C:\\Users\\spencer\\proj" : "/tmp/proj");

        path.Should().Contain(".openmono").And.Contain("agent.lock");
    }
}

public sealed class WindowsGraphArtifactsTests
{
    private static WindowsGraphInput Input(
        bool dbEntries,
        bool graphJson,
        bool report = false) => new(
            WindowsWorkspace: "/tmp/proj",
            DataDirectory: "/tmp/home/.openmono",
            DirectoryHasEntries: _ => dbEntries,
            FileExists: p => (graphJson && p.EndsWith("graph.json")) || (report && p.EndsWith("GRAPH_REPORT.md")));

    [Fact]
    public void PresentArtifactsDetected()
    {
        var status = WindowsGraphArtifacts.Check(Input(dbEntries: true, graphJson: true));

        status.CodeGraphDbPresent.Should().BeTrue();
        status.GraphifyGraphPresent.Should().BeTrue();
    }

    [Fact]
    public void ReportCountsAsGraphifyWhenJsonMissing()
    {
        var status = WindowsGraphArtifacts.Check(Input(dbEntries: false, graphJson: false, report: true));

        status.CodeGraphDbPresent.Should().BeFalse();
        status.GraphifyGraphPresent.Should().BeTrue();
        status.Detail.Should().Contain("in-container");
    }

    [Fact]
    public void EmptyWorkspaceReportsBothAbsent()
    {
        var status = WindowsGraphArtifacts.Check(Input(dbEntries: false, graphJson: false));

        status.CodeGraphDbPresent.Should().BeFalse();
        status.GraphifyGraphPresent.Should().BeFalse();
    }
}
