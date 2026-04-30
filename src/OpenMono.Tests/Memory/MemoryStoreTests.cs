using FluentAssertions;
using OpenMono.Memory;

namespace OpenMono.Tests.Memory;

public class MemoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MemoryStore _store;

    public MemoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new MemoryStore(_tempDir);
    }

    [Fact]
    public async Task SaveAndLoadAll_RoundTrips()
    {
        await _store.SaveAsync("test-memory", "user", "A test memory", "The content", CancellationToken.None);

        var entries = _store.LoadAll();
        entries.Should().HaveCount(1);
        entries[0].Name.Should().Be("test-memory");
        entries[0].Type.Should().Be("user");
        entries[0].Description.Should().Be("A test memory");
        entries[0].Content.Should().Contain("The content");
    }

    [Fact]
    public async Task Save_UpdatesIndex()
    {
        await _store.SaveAsync("my-mem", "feedback", "Some feedback", "Don't do X", CancellationToken.None);

        var index = _store.LoadIndex();
        index.Should().NotBeNull();
        index.Should().Contain("my-mem");
    }

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        await _store.SaveAsync("to-remove", "project", "Temporary", "Remove me", CancellationToken.None);
        _store.LoadAll().Should().HaveCount(1);

        await _store.RemoveAsync("to-remove", CancellationToken.None);
        _store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadIndex_NoIndex_ReturnsNull()
    {
        _store.LoadIndex().Should().BeNull();
    }

    [Fact]
    public void LoadAll_Empty_ReturnsEmpty()
    {
        _store.LoadAll().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
