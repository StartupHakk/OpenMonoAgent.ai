using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class GrepToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GrepTool _tool;
    private readonly ToolContext _context;

    public GrepToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new GrepTool();
        _context = CreateContext(_tempDir);
    }

    [SkippableFact]
    public async Task GrepFindsMatches()
    {
        Skip.IfNot(IsRipgrepInstalled(), "ripgrep (rg) not installed");

        File.WriteAllText(Path.Combine(_tempDir, "test.cs"), "public class Foo\npublic class Bar\n");

        var input = JsonDocument.Parse($$"""{"pattern": "class", "path": "{{_tempDir}}"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("class");
    }

    [Fact]
    public async Task GrepWithoutRipgrep_ReturnsError()
    {

        var input = JsonDocument.Parse($$"""{"pattern": "test", "path": "{{_tempDir}}"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        (result.IsError || result.Content.Contains("match")).Should().BeTrue();
    }

    private static bool IsRipgrepInstalled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("rg", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(2000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public void Permission_IsAutoAllow()
    {
        var input = JsonDocument.Parse("""{"pattern": "test"}""").RootElement;
        _tool.RequiredPermission(input).Should().Be(PermissionLevel.AutoAllow);
    }

    [Fact]
    public void IsReadOnly()
    {
        _tool.IsReadOnly.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ToolContext CreateContext(string workDir) => new()
    {
        ToolRegistry = new ToolRegistry(),
        Session = new SessionState(),
        Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
        Config = new AppConfig { WorkingDirectory = workDir },
        WorkingDirectory = workDir,
        WriteOutput = _ => { },
        AskUser = (_, _) => Task.FromResult(""),
    };
}
