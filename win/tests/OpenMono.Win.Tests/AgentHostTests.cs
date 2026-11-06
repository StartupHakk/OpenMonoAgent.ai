using FluentAssertions;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class WindowsShellTests
{
    [Fact]
    public void PrefersPwshThenPowershellThenCmd()
    {
        WindowsShellSelector.PreferredShells().Should().Equal("pwsh.exe", "powershell.exe", "cmd.exe");
    }

    [Fact]
    public void ResolvesFirstAvailable()
    {
        WindowsShellSelector.ResolveAvailable(name => name == "cmd.exe").Should().Be("cmd.exe");
    }

    [Fact]
    public void ThrowsWhenNothingAvailable()
    {
        var act = () => WindowsShellSelector.ResolveAvailable(_ => false);
        act.Should().Throw<InvalidOperationException>();
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
    public void DangerousCommandsDenied(string command)
    {
        WindowsGuardrails.IsDenied(command, out var reason).Should().BeTrue();
        reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("pwsh -File build.ps1")]
    [InlineData("dotnet test win/OpenMono.Win.sln")]
    [InlineData("Remove-Item C:\\Users\\spencer\\temp\\scratch -Recurse")]
    [InlineData("reg query HKLM\\Software\\Foo")]
    [InlineData("git status")]
    public void BenignCommandsPass(string command)
    {
        WindowsGuardrails.IsDenied(command, out _).Should().BeFalse();
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

public sealed class WinAgentHostTests
{
    [Fact]
    public void BuildsLlmConfigForFoundryLoopback()
    {
        var config = new WinAgentHost("qwen3.8-27b").BuildLlmConfig();

        config.Endpoint.Should().Be("http://127.0.0.1:7474");
        config.Model.Should().Be("qwen3.8-27b");
    }

    [Fact]
    public void CreatesOpenAiCompatClient()
    {
        using var client = new WinAgentHost("qwen3.8-27b").CreateLlmClient();

        client.Should().NotBeNull();
    }

    [Fact]
    public void AppliesEnvironmentContract()
    {
        WinAgentHost.ApplyEnvironment("qwen3.8-27b");

        Environment.GetEnvironmentVariable(OmaEndpointContract.EndpointEnvVar)
            .Should().Be("http://127.0.0.1:7474");
        Environment.GetEnvironmentVariable(OmaEndpointContract.ModelEnvVar)
            .Should().Be("qwen3.8-27b");

        Environment.SetEnvironmentVariable(OmaEndpointContract.EndpointEnvVar, null);
        Environment.SetEnvironmentVariable(OmaEndpointContract.ModelEnvVar, null);
    }

    [Fact]
    public void PromptOverlayMentionsWindowsAndFoundry()
    {
        WindowsPromptOverlay.OverlayText.Should().Contain("PowerShell").And.Contain("127.0.0.1:7474");
    }
}
