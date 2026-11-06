using FluentAssertions;
using Xunit;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class FoundryVersionTests
{
    [Theory]
    [InlineData("foundry version 0.13.0", "0.13.0", true)]
    [InlineData("0.13.2", "0.13.2", true)]
    [InlineData("Foundry Local 0.14.0 (build abc)", "0.14.0", true)]
    [InlineData("foundry version 0.12.9", "0.12.9", false)]
    public void ParsesCliOutputAndGatesMinimum(string output, string expected, bool supported)
    {
        FoundryVersion.TryParse(output, out var version).Should().BeTrue();
        version.ToString().Should().Be(expected);
        FoundryVersion.IsSupported(version).Should().Be(supported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no version here")]
    public void UnparseableOutputFails(string? output)
    {
        FoundryVersion.TryParse(output, out _).Should().BeFalse();
    }
}

public sealed class FoundryCliDetectorTests
{
    private static string Exe => FoundryCliDetector.CliExecutableName;

    [Fact]
    public async Task DetectsSupportedCli()
    {
        var runner = new FakeCliRunner();
        runner.Respond(Exe, "--version", 0, "foundry version 0.13.2\n");

        var status = await new FoundryCliDetector(runner).DetectAsync();

        status.Found.Should().BeTrue();
        status.IsSupported.Should().BeTrue();
        status.Version.Should().Be(new Version(0, 13, 2));
    }

    [Fact]
    public async Task OldCliIsFoundButUnsupported()
    {
        var runner = new FakeCliRunner();
        runner.Respond(Exe, "--version", 0, "foundry version 0.12.0\n");

        var status = await new FoundryCliDetector(runner).DetectAsync();

        status.Found.Should().BeTrue();
        status.IsSupported.Should().BeFalse();
    }

    [Fact]
    public async Task MissingCliReportsNotFound()
    {
        var runner = new FakeCliRunner();
        // No response queued: RunAsync throws like a missing executable would.
        var detector = new FoundryCliDetector(runner);

        var status = await detector.DetectAsync();

        status.Found.Should().BeFalse();
        status.IsSupported.Should().BeFalse();
    }

    [Fact]
    public async Task ServerStatusExtractsUrl()
    {
        var runner = new FakeCliRunner();
        runner.Respond(Exe, "server status", 0, "Serving on http://127.0.0.1:7474 (model qwen3.8-27b)\n");

        var status = await new FoundryCliDetector(runner).GetServerStatusAsync();

        status.Running.Should().BeTrue();
        status.Url.Should().Be("http://127.0.0.1:7474");
    }

    [Fact]
    public async Task ServerStatusWithoutUrlMeansNotRunning()
    {
        var runner = new FakeCliRunner();
        runner.Respond(Exe, "server status", 1, "", "no server running");

        var status = await new FoundryCliDetector(runner).GetServerStatusAsync();

        status.Running.Should().BeFalse();
        status.Url.Should().BeNull();
    }
}
