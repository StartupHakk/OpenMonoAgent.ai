using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class ApplyPatchToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ApplyPatchTool _tool;
    private readonly ToolContext _context;

    public ApplyPatchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new ApplyPatchTool();
        _context = CreateContext(_tempDir);
    }

    [Fact]
    public async Task ApplyPatch_ModifiesFile()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "line1\nline2\nline3\n");

        var patch = $"""
            --- a/test.txt
            +++ b/test.txt
            @@ -1,3 +1,3 @@
             line1
            -line2
            +line2_modified
             line3
            """;
        var input = JsonDocument.Parse($$"""{"patch": "{{patch.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}}"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("1 file(s) modified");
    }

    [Fact]
    public async Task DryRun_DoesNotModify()
    {
        var filePath = Path.Combine(_tempDir, "dry.txt");
        File.WriteAllText(filePath, "original\n");

        var patch = "--- a/dry.txt\n+++ b/dry.txt\n@@ -1,1 +1,1 @@\n-original\n+modified\n";
        var escapedPatch = patch.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        var input = JsonDocument.Parse($$$"""{"patch": "{{{escapedPatch}}}", "dry_run": true}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.Content.Should().Contain("Would modify");
        (await File.ReadAllTextAsync(filePath)).Should().Be("original\n");
    }

    [Fact]
    public async Task EmptyPatch_ReturnsError()
    {
        var input = JsonDocument.Parse("""{"patch": ""}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("No valid hunks");
    }

    [Fact]
    public void Permission_IsAsk()
    {
        var input = JsonDocument.Parse("""{"patch": "test"}""").RootElement;
        _tool.RequiredPermission(input).Should().Be(PermissionLevel.Ask);
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
