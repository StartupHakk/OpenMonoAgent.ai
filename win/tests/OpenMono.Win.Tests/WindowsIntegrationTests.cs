using FluentAssertions;
using Xunit;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class WindowsImagePolicyTests
{
    [Fact]
    public void TextTurnRoutesToTarget()
    {
        var route = WindowsImagePolicy.RouteImageTurn(
            hasImages: false, "qwen3.8-27b", null, "qwen2.5-vl-7b");

        route.ModelAlias.Should().Be("qwen3.8-27b");
    }

    [Fact]
    public void RequireVisionModelReturnsRoutedModel()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            true, "qwen3.8-27b", "qwen2.5-vl-7b",
            new VisionProbeResult(false, 400, "qwen3.8-27b", "no images"), "qwen2.5-vl-7b");

        WindowsImagePolicy.RequireVisionModel(route, "qwen2.5-vl-7b").Should().Be("qwen2.5-vl-7b");
    }

    [Fact]
    public void RequireVisionModelThrowsWithRecoverySteps()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            true, "qwen3.8-27b", "qwen2.5-vl-7b",
            new VisionProbeResult(false, 400, "qwen3.8-27b", "no images"), null);

        var act = () => WindowsImagePolicy.RequireVisionModel(route, "qwen2.5-vl-7b");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*foundry model load qwen2.5-vl-7b*vision_enabled*");
    }
}

public sealed class WindowsVisionConfigTests
{
    [Fact]
    public void ExplicitSettingBeatsProbe()
    {
        var accepted = new VisionProbeResult(true, 200, "qwen3.8-27b", "ok");

        WindowsVisionConfig.ApplyProbeResult(false, accepted).VisionEnabled.Should().BeFalse();
        WindowsVisionConfig.ApplyProbeResult(true, null).VisionEnabled.Should().BeTrue();
    }

    [Fact]
    public void AcceptedProbeEnablesVision()
    {
        var accepted = new VisionProbeResult(true, 200, "qwen3.8-27b", "ok");

        var resolution = WindowsVisionConfig.ApplyProbeResult(null, accepted);

        resolution.VisionEnabled.Should().BeTrue();
        resolution.Reason.Should().Contain("qwen3.8-27b");
    }

    [Fact]
    public void RefusedProbeDisablesVision()
    {
        var refused = new VisionProbeResult(false, 400, "qwen3.8-27b", "image input not supported");

        var resolution = WindowsVisionConfig.ApplyProbeResult(null, refused);

        resolution.VisionEnabled.Should().BeFalse();
        resolution.Reason.Should().Contain("inference.vision_enabled=true");
    }
}

public sealed class WindowsAcpHostTests
{
    [Fact]
    public void PrepareSetsWorkspaceAndKeepsAcpContract()
    {
        var savedWorkspace = Environment.GetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar);
        var savedPort = Environment.GetEnvironmentVariable(WindowsAcpHost.HostPortEnvVar);
        Environment.SetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar, null);
        Environment.SetEnvironmentVariable(WindowsAcpHost.HostPortEnvVar, null);
        try
        {
            var workspace = OperatingSystem.IsWindows() ? "C:\\Users\\spencer\\proj" : "/tmp/oma-win-proj";
            var prep = WindowsAcpHost.Prepare(workspace);

            prep.Settings.Enabled.Should().BeTrue();
            prep.Settings.Port.Should().Be(7475);
            prep.Settings.BindAllInterfaces.Should().BeFalse();
            prep.Workspace.Should().Be(workspace);
            prep.WorkspaceEnvWasAlreadySet.Should().BeFalse();
            prep.LockFile.LockFilePath.Should().Contain(".openmono");
            Environment.GetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar)
                .Should().Be(workspace);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar, savedWorkspace);
            Environment.SetEnvironmentVariable(WindowsAcpHost.HostPortEnvVar, savedPort);
        }
    }

    [Fact]
    public void ExistingExtensionEnvIsLeftAlone()
    {
        var saved = Environment.GetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar);
        Environment.SetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar, "/ext/workspace");
        try
        {
            var prep = WindowsAcpHost.Prepare("/other/workspace");

            prep.WorkspaceEnvWasAlreadySet.Should().BeTrue();
            prep.Workspace.Should().Be("/ext/workspace");
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowsAcpHost.HostWorkspaceEnvVar, saved);
        }
    }
}

public sealed class WindowsMcpLauncherTests
{
    private static string? FindNothing(string name) => null;

    [Fact]
    public void ExeLaunchesDirect()
    {
        var resolved = WindowsMcpLauncher.Resolve(
            "code-review-graph.exe", name => name == "code-review-graph.exe" ? "C:\\tools\\code-review-graph.exe" : null);

        resolved.DirectLaunch.Should().BeTrue();
        resolved.FileName.Should().Contain("code-review-graph.exe");
        resolved.ArgsPrefix.Should().BeEmpty();
    }

    [Fact]
    public void CmdShimWrapsInCmdExe()
    {
        var resolved = WindowsMcpLauncher.Resolve(
            "code-review-graph", name => name == "code-review-graph.cmd" ? "C:\\tools\\code-review-graph.cmd" : null);

        resolved.DirectLaunch.Should().BeFalse();
        resolved.FileName.Should().Be("cmd.exe");
        resolved.ArgsPrefix.Should().Contain("code-review-graph.cmd");
    }

    [Fact]
    public void UnknownCommandPassesThrough()
    {
        var resolved = WindowsMcpLauncher.Resolve("some-mcp-server", FindNothing);

        resolved.DirectLaunch.Should().BeTrue();
        resolved.FileName.Should().Be("some-mcp-server");
    }
}

public sealed class WindowsGraphHostTests
{
    [Fact]
    public async Task FindsExeCandidateFirst()
    {
        var runner = new FakeCliRunner();
        runner.Respond("code-review-graph.exe", "--version", 0, "code-review-graph 0.9.1\n");

        var status = await WindowsGraphHost.ProbeCodeReviewGraphAsync(runner);

        status.Available.Should().BeTrue();
        status.Command.Should().Be("code-review-graph.exe");
        status.Version.Should().Be("0.9.1");
    }

    [Fact]
    public async Task FallsThroughToBareName()
    {
        var runner = new FakeCliRunner();
        runner.Respond("code-review-graph", "--version", 0, "code-review-graph 0.9.1\n");

        var status = await WindowsGraphHost.ProbeCodeReviewGraphAsync(runner);

        status.Available.Should().BeTrue();
        status.Command.Should().Be("code-review-graph");
    }

    [Fact]
    public async Task MissingToolDisablesGraph()
    {
        var runner = new FakeCliRunner();

        var status = await WindowsGraphHost.ProbeCodeReviewGraphAsync(runner);

        status.Available.Should().BeFalse();
        status.Command.Should().BeNull();
    }

    [Fact]
    public void GraphifyRunsThroughWindowsShell()
    {
        var (shell, args) = WindowsGraphHost.GraphifyInvocation(
            "query \"auth flow\"", name => name == "pwsh.exe");

        shell.Should().Be("pwsh.exe");
        args.Should().StartWith("graphify ");
    }

    [Fact]
    public void BashDenyPatternsTargetBashTool()
    {
        WindowsGuardrails.DenyPatterns("Bash").Should().NotBeEmpty();
        WindowsGuardrails.DenyPatterns("FileRead").Should().BeEmpty();
    }
}
