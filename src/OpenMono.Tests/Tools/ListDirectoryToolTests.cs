using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class ListDirectoryToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ListDirectoryTool _tool;
    private readonly ToolContext _context;

    public ListDirectoryToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new ListDirectoryTool();
        _context = CreateContext(_tempDir);
    }

    [Fact]
    public async Task ListDirectory_ShowsFilesAndDirs()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "content");
        Directory.CreateDirectory(Path.Combine(_tempDir, "subdir"));

        var input = JsonDocument.Parse($$"""{"path": "{{_tempDir}}"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("file.txt");
        result.Content.Should().Contain("subdir");
    }

    [Fact]
    public async Task ListDirectory_NonExistent_ReturnsError()
    {
        var input = JsonDocument.Parse("""{"path": "/nonexistent/dir"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void Permission_IsAutoAllow()
    {
        var input = JsonDocument.Parse("""{"path": "/tmp"}""").RootElement;
        _tool.RequiredPermission(input).Should().Be(PermissionLevel.AutoAllow);
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
