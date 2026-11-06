using System.Text.Json;
using FluentAssertions;
using OpenMono.Win.Launcher;
using Xunit;

namespace OpenMono.Win.Tests;

public static class RepoPaths
{
    public static string WinDir { get; } = Find();

    public static string RepoRoot => Path.GetDirectoryName(WinDir)!;

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "win", "scripts", "check-cuda.ps1")))
            {
                return Path.Combine(dir.FullName, "win");
            }
        }

        throw new DirectoryNotFoundException("win/ not found above the test output directory.");
    }
}

public sealed class HostPrerequisiteTests
{
    [Fact]
    public void MinimumSdkMatchesGlobalJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.WinDir, "global.json")));
        var pinned = new Version(doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()!);

        HostPrerequisites.MinDotnetSdk.Should().Be(pinned);
        HostPrerequisites.DotnetWingetId.Should().Be("Microsoft.DotNet.SDK.10");
    }

    [Fact]
    public void UsableSdkNeedsTheSameMajorAndThePinnedPatch()
    {
        var sdks = HostPrerequisites.ParseSdkList(
            "8.0.404 [C:\\Program Files\\dotnet\\sdk]\r\n10.0.100 [C:\\Program Files\\dotnet\\sdk]\r\n"
            + "10.0.403 [C:\\Program Files\\dotnet\\sdk]\r\n10.0.500-preview.1 [C:\\x]\r\n11.0.100 [C:\\Program Files\\dotnet\\sdk]\r\n");

        sdks.Should().HaveCount(5);
        HostPrerequisites.UsableSdk(sdks).Should().Be(new Version(10, 0, 500));
        HostPrerequisites.UsableSdk([new Version(10, 0, 100), new Version(11, 0, 100)]).Should().BeNull();
    }

    [Theory]
    [InlineData("git version 2.51.0.windows.1", "2.51.0")]
    [InlineData("git version 2.39.5 (Apple Git-154)", "2.39.5")]
    public void ParsesGitVersions(string output, string expected)
    {
        HostPrerequisites.ParseGitVersion(output).Should().Be(new Version(expected));
    }

    [Fact]
    public void SetupScriptsInstallBothWithWingetAndAFallback()
    {
        var prereqs = File.ReadAllText(Path.Combine(RepoPaths.WinDir, "scripts", "prereqs.ps1"));

        prereqs.Should().Contain("'Git.Git'").And.Contain("'Microsoft.DotNet.SDK.' + (Get-RequiredDotnetSdk).Major")
            .And.Contain("https://dot.net/v1/dotnet-install.ps1")
            .And.Contain("https://api.github.com/repos/git-for-windows/git/releases/latest")
            .And.Contain("--accept-package-agreements");
        File.ReadAllText(Path.Combine(RepoPaths.WinDir, "openmono.ps1")).Should().Contain("Confirm-HostPrerequisites");
    }
}

// The agent runs in a Linux container: anything it executes or parses there
// must be LF, or the entrypoint fails with "/usr/bin/env: 'bash\r'". On a
// Windows CI checkout (core.autocrlf=true) these fail without .gitattributes.
public sealed class ContainerLineEndingTests
{
    private static IEnumerable<string> ContainerBoundFiles()
    {
        var root = RepoPaths.RepoRoot;
        var docker = Directory.EnumerateFiles(Path.Combine(root, "docker"), "*", SearchOption.AllDirectories);
        var shell = Directory.EnumerateFiles(root, "*.sh", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is ".git" or "node_modules" or "bin" or "obj"));
        return docker.Concat(shell).Append(Path.Combine(root, "openmono")).Distinct();
    }

    [Fact]
    public void ContainerBoundFilesHaveNoCarriageReturns()
    {
        var files = ContainerBoundFiles().ToList();
        files.Should().Contain(f => f.EndsWith("entrypoint.sh", StringComparison.Ordinal));

        var withCr = files.Where(f => File.ReadAllBytes(f).Contains((byte)'\r')).ToList();

        withCr.Should().BeEmpty("files run or parsed inside the Linux containers must use LF line endings");
    }

    [Fact]
    public void EntrypointShebangIsLf()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoPaths.RepoRoot, "docker", "entrypoint.sh"));
        var firstLine = bytes.TakeWhile(b => b != (byte)'\n').ToArray();

        System.Text.Encoding.ASCII.GetString(firstLine).Should().Be("#!/usr/bin/env bash");
    }

    [Fact]
    public void GitattributesPinsContainerFilesToLf()
    {
        var rules = File.ReadAllLines(Path.Combine(RepoPaths.RepoRoot, ".gitattributes"))
            .Select(l => System.Text.RegularExpressions.Regex.Split(l.Trim(), @"\s+"))
            .Where(parts => parts.Length > 1 && !parts[0].StartsWith('#'))
            .ToDictionary(parts => parts[0], parts => parts.Skip(1).ToArray());

        foreach (var pattern in new[] { "*.sh", "*.yml", "*.yaml", "Dockerfile", "Dockerfile.*", "Caddyfile", "docker/**", "openmono" })
        {
            rules.Should().ContainKey(pattern);
            rules[pattern].Should().Contain("eol=lf");
        }
    }
}
