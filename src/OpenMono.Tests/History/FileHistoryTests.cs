using FluentAssertions;
using OpenMono.Config;
using OpenMono.History;

namespace OpenMono.Tests.History;

public class FileHistoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileHistory _history;

    public FileHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var config = new AppConfig { DataDirectory = _tempDir };
        _history = new FileHistory(config);
    }

    [Fact]
    public void RecordBeforeAndAfter_CapturesSnapshot()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "original");

        _history.RecordBefore(filePath, "FileEdit", 0);
        File.WriteAllText(filePath, "modified");
        _history.RecordAfter(filePath);

        _history.Snapshots.Should().HaveCount(1);
        _history.Snapshots[0].ContentBefore.Should().Be("original");
        _history.Snapshots[0].ContentAfter.Should().Be("modified");
        _history.Snapshots[0].IsCreation.Should().BeFalse();
    }

    [Fact]
    public void RecordBefore_NewFile_IsCreation()
    {
        var filePath = Path.Combine(_tempDir, "new-file.txt");

        _history.RecordBefore(filePath, "FileWrite", 0);
        File.WriteAllText(filePath, "new content");
        _history.RecordAfter(filePath);

        _history.Snapshots[0].IsCreation.Should().BeTrue();
        _history.Snapshots[0].ContentBefore.Should().BeNull();
    }

    [Fact]
    public async Task TrackAsync_RecordsBothStates()
    {
        var filePath = Path.Combine(_tempDir, "tracked.txt");
        File.WriteAllText(filePath, "before");

        var result = await _history.TrackAsync(filePath, "FileEdit", 0, async () =>
        {
            await File.WriteAllTextAsync(filePath, "after");
            return "done";
        });

        result.Should().Be("done");
        _history.Snapshots.Should().HaveCount(1);
        _history.Snapshots[0].ContentBefore.Should().Be("before");
        _history.Snapshots[0].ContentAfter.Should().Be("after");
    }

    [Fact]
    public async Task RevertAsync_RestoresOriginalContent()
    {
        var filePath = Path.Combine(_tempDir, "revert.txt");
        File.WriteAllText(filePath, "original");

        _history.RecordBefore(filePath, "FileEdit", 0);
        File.WriteAllText(filePath, "modified");
        _history.RecordAfter(filePath);

        var reverted = await _history.RevertAsync(1, CancellationToken.None);

        reverted.Should().HaveCount(1);
        (await File.ReadAllTextAsync(filePath)).Should().Be("original");
    }

    [Fact]
    public async Task RevertAsync_DeletesCreatedFiles()
    {
        var filePath = Path.Combine(_tempDir, "created.txt");

        _history.RecordBefore(filePath, "FileWrite", 0);
        File.WriteAllText(filePath, "new content");
        _history.RecordAfter(filePath);

        await _history.RevertAsync(1, CancellationToken.None);

        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public void GetRecentChanges_ReturnsFormattedList()
    {
        var filePath = Path.Combine(_tempDir, "recent.txt");
        File.WriteAllText(filePath, "content");

        _history.RecordBefore(filePath, "FileEdit", 0);
        File.WriteAllText(filePath, "updated");
        _history.RecordAfter(filePath);

        var changes = _history.GetRecentChanges();
        changes.Should().HaveCount(1);
        changes[0].Should().Contain("FileEdit");
        changes[0].Should().Contain("Modified");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
