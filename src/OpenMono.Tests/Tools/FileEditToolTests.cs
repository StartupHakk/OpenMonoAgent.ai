using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class FileEditToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileEditTool _tool;
    private readonly ToolContext _context;

    public FileEditToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new FileEditTool();
        _context = CreateContext(_tempDir);
    }

    [Fact]
    public async Task ReplaceUnique_Succeeds()
    {
        var filePath = Path.Combine(_tempDir, "test.cs");
        await File.WriteAllTextAsync(filePath, "var x = 1;\nvar y = 2;\nvar z = 3;");

        var input = JsonDocument.Parse($$"""
        {"file_path": "{{filePath}}", "old_string": "var y = 2;", "new_string": "var y = 42;"}
        """).RootElement;

        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("var y = 42;");
        content.Should().Contain("var x = 1;");
    }

    [Fact]
    public async Task ReplaceDuplicate_FailsWithoutReplaceAll()
    {
        var filePath = Path.Combine(_tempDir, "dup.cs");
        await File.WriteAllTextAsync(filePath, "hello world\nhello world");

        var input = JsonDocument.Parse($$"""
        {"file_path": "{{filePath}}", "old_string": "hello world", "new_string": "goodbye"}
        """).RootElement;

        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("2 times");
    }

    [Fact]
    public async Task ReplaceAll_ReplacesAllOccurrences()
    {
        var filePath = Path.Combine(_tempDir, "all.cs");
        await File.WriteAllTextAsync(filePath, "foo bar foo baz foo");

        var input = JsonDocument.Parse($$"""
        {"file_path": "{{filePath}}", "old_string": "foo", "new_string": "qux", "replace_all": true}
        """).RootElement;

        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Be("qux bar qux baz qux");
    }

    [Fact]
    public async Task EmptyOldString_ReturnsErrorImmediately_DoesNotHang()
    {
        var filePath = Path.Combine(_tempDir, "empty.cs");
        await File.WriteAllTextAsync(filePath, "var x = 1;");

        var input = JsonDocument.Parse($$"""
        {"file_path": "{{filePath}}", "old_string": "", "new_string": "anything"}
        """).RootElement;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await _tool.ExecuteAsync(input, _context, cts.Token);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("must not be empty");

        (await File.ReadAllTextAsync(filePath)).Should().Be("var x = 1;");
    }

    [Fact]
    public async Task IdenticalOldAndNew_ReturnsError()
    {
        var filePath = Path.Combine(_tempDir, "noop.cs");
        await File.WriteAllTextAsync(filePath, "var x = 1;");

        var input = JsonDocument.Parse($$"""
        {"file_path": "{{filePath}}", "old_string": "var x = 1;", "new_string": "var x = 1;"}
        """).RootElement;

        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("identical");
    }

    [Fact]
    public async Task ReplaceNonExistent_ReturnsError()
    {
        var filePath = Path.Combine(_tempDir, "missing.cs");
        await File.WriteAllTextAsync(filePath, "hello world");

        var input = JsonDocument.Parse($$"""
        {"file_path": "{{filePath}}", "old_string": "not here", "new_string": "replacement"}
        """).RootElement;

        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
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
