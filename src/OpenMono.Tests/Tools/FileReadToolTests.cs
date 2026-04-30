using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class FileReadToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileReadTool _tool;
    private readonly ToolContext _context;

    public FileReadToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new FileReadTool();
        _context = CreateContext(_tempDir);
    }

    [Fact]
    public async Task ReadExistingFile_ReturnsContentsWithLineNumbers()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "line one\nline two\nline three");

        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("1\tline one");
        result.Content.Should().Contain("2\tline two");
        result.Content.Should().Contain("3\tline three");
    }

    [Fact]
    public async Task ReadNonExistentFile_ReturnsError()
    {
        var input = JsonDocument.Parse("""{"file_path": "/nonexistent/file.txt"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task ReadWithOffsetAndLimit_ReturnsSubset()
    {
        var filePath = Path.Combine(_tempDir, "lines.txt");
        await File.WriteAllTextAsync(filePath, "a\nb\nc\nd\ne");

        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}", "offset": 1, "limit": 2}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("2\tb");
        result.Content.Should().Contain("3\tc");
        result.Content.Should().NotContain("1\ta");
        result.Content.Should().NotContain("4\td");
    }

    [Fact]
    public void Permission_IsAutoAllow()
    {
        var input = JsonDocument.Parse("""{"file_path": "test.txt"}""").RootElement;
        _tool.RequiredPermission(input).Should().Be(PermissionLevel.AutoAllow);
    }

    [Fact]
    public void IsConcurrencySafe_ReturnsTrue()
    {
        _tool.IsConcurrencySafe.Should().BeTrue();
    }

    [Fact]
    public async Task ReadSameFile_SecondRead_ReturnsFileUnchanged()
    {

        FileReadTool.ClearCache();

        var filePath = Path.Combine(_tempDir, "dedup-test.txt");
        await File.WriteAllTextAsync(filePath, "some content\nmore lines");

        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}"}""").RootElement;

        var result1 = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        result1.IsError.Should().BeFalse();
        result1.Content.Should().Contain("1\tsome content");
        result1.Content.Should().NotContain("file_unchanged");

        var result2 = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        result2.IsError.Should().BeFalse();
        result2.Content.Should().Contain("file_unchanged");
    }

    [Fact]
    public async Task ReadModifiedFile_ReturnsNewContent()
    {

        FileReadTool.ClearCache();

        var filePath = Path.Combine(_tempDir, "modified-test.txt");
        await File.WriteAllTextAsync(filePath, "original content");

        var input = JsonDocument.Parse($$"""{"file_path": "{{filePath}}"}""").RootElement;

        var result1 = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        result1.Content.Should().Contain("original content");

        await Task.Delay(10);
        await File.WriteAllTextAsync(filePath, "modified content");

        var result2 = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        result2.IsError.Should().BeFalse();
        result2.Content.Should().Contain("modified content");
        result2.Content.Should().NotContain("file_unchanged");
    }

    public void Dispose()
    {
        FileReadTool.ClearCache();
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
