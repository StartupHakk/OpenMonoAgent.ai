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
    private static WindowsStackConfig Config() => new(
        WorkDir: "C:\\Users\\spencer\\proj",
        HomeDir: "C:\\Users\\spencer",
        ComposeFile: "C:\\openmono\\docker\\docker-compose.yml",
        InferenceEndpoint: "http://127.0.0.1:7474",
        Model: "qwen3.8-27b");

    [Fact]
    public void EndpointReachesHostFoundry()
    {
        Config().ContainerEndpoint.Should().Be("http://host.docker.internal:7474");
    }

    [Fact]
    public void DockerArgsMirrorAgentContainerShape()
    {
        var args = WindowsContainerStack.ToDockerArgs(Config());

        args.Should().Contain("compose -f");
        args.Should().Contain("-p 127.0.0.1:7475:7475");
        args.Should().Contain("-v \"C:\\Users\\spencer\\proj:/workspace\"");
        args.Should().Contain("-e OPENMONO_ENDPOINT=http://host.docker.internal:7474");
        args.Should().Contain("-e OPENMONO_MODEL=qwen3.8-27b");
        args.Should().Contain("-e OPENMONO_HOST_OS=Windows");
        args.Should().Contain("-e HOST_ACP_PORT=7475");
        args.Should().Contain("agent --acp-only --acp-port 7475");
        args.Should().NotContain("llama-server");
        args.Should().NotContain("OPENMONO_API_KEY=");
    }

    [Fact]
    public void ApiKeyTravelsAsBareEnvFlag()
    {
        var config = Config() with { ApiKey = "secret" };
        var env = WindowsContainerStack.EnvironmentArgs(config);

        env.Should().Contain("-e OPENMONO_API_KEY");
        WindowsContainerStack.ToDockerArgs(config).Should().NotContain("secret");
    }

    [Fact]
    public void DiscoveryUrlPointsAtPublishedAcp()
    {
        WindowsContainerStack.DiscoveryUrl(Config()).Should().Be("http://127.0.0.1:7475/api/v1/discovery");
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
