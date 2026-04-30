using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class GlobToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GlobTool _tool;
    private readonly ToolContext _context;

    public GlobToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        _tool = new GlobTool();
        _context = CreateContext(_tempDir);

        File.WriteAllText(Path.Combine(_tempDir, "readme.md"), "# Test");
        File.WriteAllText(Path.Combine(_tempDir, "src", "app.cs"), "class App {}");
        File.WriteAllText(Path.Combine(_tempDir, "src", "test.cs"), "class Test {}");
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), "{}");
    }

    [Fact]
    public async Task GlobCsFiles_FindsMatches()
    {
        var input = JsonDocument.Parse($$"""{"pattern": "**

*.xyz", "path": "{{_tempDir}}"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("No files matching");
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
