using FluentAssertions;
using OpenMono.Playbooks;

namespace OpenMono.Tests.Playbooks;

public class PlaybookStateTests : IDisposable
{
    private readonly string _tempDir;

    public PlaybookStateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CompleteStep_TracksCompletion()
    {
        var state = new PlaybookState
        {
            PlaybookName = "test",
            SessionId = "abc123",
        };

        state.IsStepCompleted("analyze").Should().BeFalse();
        state.CompleteStep("analyze", "analysis output");

        state.IsStepCompleted("analyze").Should().BeTrue();
        state.StepOutputs["analyze"].Should().Be("analysis output");
        state.CompletedSteps.Should().Contain("analyze");
        state.CurrentStepId.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_WritesFile()
    {
        var state = new PlaybookState
        {
            PlaybookName = "commit",
            SessionId = "test123",
            Parameters = new() { ["message"] = "fix bug" },
        };
        state.CompleteStep("analyze", "found the bug");

        await state.SaveAsync(_tempDir, CancellationToken.None);

        var path = Path.Combine(_tempDir, "playbook-state", "commit_test123.json");
        File.Exists(path).Should().BeTrue();

        var json = await File.ReadAllTextAsync(path);
        json.Should().Contain("commit");
        json.Should().Contain("analyze");
    }

    [Fact]
    public async Task LoadAsync_DeserializesBasicFields()
    {
        var state = new PlaybookState
        {
            PlaybookName = "review",
            SessionId = "abc",
        };
        await state.SaveAsync(_tempDir, CancellationToken.None);

        var loaded = await PlaybookState.LoadAsync(_tempDir, "review", "abc", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.PlaybookName.Should().Be("review");
        loaded.SessionId.Should().Be("abc");
    }

    [Fact]
    public async Task Load_NonExistent_ReturnsNull()
    {
        var result = await PlaybookState.LoadAsync(_tempDir, "nonexistent", "xxx", CancellationToken.None);
        result.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
