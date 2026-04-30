using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.History;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class FileWriteToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileWriteTool _tool;
    private readonly ToolContext _context;
    private readonly FileHistory _history;

    public FileWriteToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new FileWriteTool();
        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        _history = new FileHistory(config);
        _context = CreateContext(_tempDir, _history);
    }

    [Fact]
    public async Task CreateNewFile_Succeeds()
    {
        var filePath = Path.Combine(_tempDir, "new.txt");
        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}", "content": "hello world"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Created");
        (await File.ReadAllTextAsync(filePath)).Should().Be("hello world");
    }

    [Fact]
    public async Task OverwriteFile_Succeeds()
    {
        var filePath = Path.Combine(_tempDir, "existing.txt");
        File.WriteAllText(filePath, "original");

        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}", "content": "updated"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Overwrote");
    }

    [Fact]
    public async Task CreatesParentDirectories()
    {
        var filePath = Path.Combine(_tempDir, "nested", "dir", "file.txt");
        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}", "content": "deep"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task TracksFileHistory()
    {
        var filePath = Path.Combine(_tempDir, "tracked.txt");
        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}", "content": "new content"}""").RootElement;
        await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        _history.Snapshots.Should().HaveCount(1);
        _history.Snapshots[0].IsCreation.Should().BeTrue();
    }

    [Fact]
    public void Permission_IsAsk()
    {
        var input = JsonDocument.Parse("""{"file_path": "test.txt", "content": "x"}""").RootElement;
        _tool.RequiredPermission(input).Should().Be(PermissionLevel.Ask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ToolContext CreateContext(string workDir, FileHistory history) => new()
    {
        ToolRegistry = new ToolRegistry(),
        Session = new SessionState(),
        Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
        Config = new AppConfig { WorkingDirectory = workDir },
        WorkingDirectory = workDir,
        WriteOutput = _ => { },
        AskUser = (_, _) => Task.FromResult(""),
        FileHistory = history,
    };
}
