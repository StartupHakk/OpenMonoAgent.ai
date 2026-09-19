using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class SpecialistToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SpecialistTool _tool;

    public SpecialistToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "fixme.cs"), "// TODO: fix null handling\nclass A {}");
        File.WriteAllText(Path.Combine(_tempDir, "widget_test.cs"), "class WidgetTest {}");
        File.WriteAllText(Path.Combine(_tempDir, "README.md"), "# Docs");
        File.WriteAllText(Path.Combine(_tempDir, "image.png"), "binary");
        _tool = new SpecialistTool(new DecisionOptions(true, 0.85, 0.6, 0.5, 64), _tempDir);
    }

    private ToolContext Context() => new()
    {
        ToolRegistry = new ToolRegistry(),
        Session = new SessionState(),
        Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
        Config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir },
        WorkingDirectory = _tempDir,
        WriteOutput = _ => { },
        AskUser = (_, _) => Task.FromResult(""),
    };

    [Fact]
    public async Task Triage_ClassifiesFilesByNameAndMarkers()
    {
        var input = JsonDocument.Parse("""{"task": "triage-files"}""").RootElement;

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);
        var report = (OpenMono.Decisions.DecisionReport)result.MachinePayload!;

        result.IsError.Should().BeFalse();
        report.AllDecisions.Should().HaveCount(4);
        report.AllDecisions.Single(d => d.Id == "fixme.cs").Action.Should().Be("fix");
        report.AllDecisions.Single(d => d.Id == "widget_test.cs").Action.Should().Be("test");
        report.AllDecisions.Single(d => d.Id == "README.md").Action.Should().Be("docs");
        report.AllDecisions.Single(d => d.Id == "image.png").Action.Should().Be("skip");
        result.Content.Should().NotContain("TODO");
    }

    [Fact]
    public async Task CommitReady_SkipsSecretsAndStagesCode()
    {
        File.WriteAllText(Path.Combine(_tempDir, "keys.txt"), "AKIAIOSFODNN7EXAMPLE");
        var input = JsonDocument.Parse("""{"task": "commit-ready"}""").RootElement;

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);
        var report = (OpenMono.Decisions.DecisionReport)result.MachinePayload!;

        report.AllDecisions.Single(d => d.Id == "keys.txt").Action.Should().Be("skip");
        report.AllDecisions.Single(d => d.Id == "fixme.cs").Action.Should().Be("stage");
        result.Content.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
    }

    [Fact]
    public async Task RejectsFillFormTask()
    {
        var input = JsonDocument.Parse("""{"task": "fill-form"}""").RootElement;

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RespectsScopeAndMaxSteps()
    {
        var input = JsonDocument.Parse("""{"task": "triage-files", "scope": "*.md", "max_steps": 64}""").RootElement;

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);
        var report = (OpenMono.Decisions.DecisionReport)result.MachinePayload!;

        report.AllDecisions.Select(d => d.Id).Should().Equal("README.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
