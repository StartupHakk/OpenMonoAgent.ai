using FluentAssertions;
using OpenMono.Config;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var config = new AppConfig { DataDirectory = _tempDir };
        _manager = new SessionManager(config);
    }

    [Fact]
    public void CreateSession_ReturnsNewSession()
    {
        var session = SessionManager.CreateSession();
        session.Should().NotBeNull();
        session.Id.Should().HaveLength(12);
        session.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Hello" });
        session.AddMessage(new Message { Role = MessageRole.Assistant, Content = "Hi there!" });

        await _manager.SaveAsync(session, CancellationToken.None);

        var loaded = await _manager.LoadAsync(session.Id, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(2);
        loaded.Messages[0].Role.Should().Be(MessageRole.User);
        loaded.Messages[0].Content.Should().Be("Hello");
        loaded.Messages[1].Role.Should().Be(MessageRole.Assistant);
        loaded.Messages[1].Content.Should().Be("Hi there!");
    }

    [Fact]
    public async Task LoadNonExistent_ReturnsNull()
    {
        var loaded = await _manager.LoadAsync("nonexistent", CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ListSessions_OnlyReturnsSessionsFromSameDirectory()
    {

        var managerA = new SessionManager(new AppConfig
        {
            DataDirectory = _tempDir,
            WorkingDirectory = "/project/alpha"
        });
        var managerB = new SessionManager(new AppConfig
        {
            DataDirectory = _tempDir,
            WorkingDirectory = "/project/beta"
        });

        var sessionA = SessionManager.CreateSession();
        sessionA.AddMessage(new Message { Role = MessageRole.User, Content = "From alpha" });
        await managerA.SaveAsync(sessionA, CancellationToken.None);

        var sessionB = SessionManager.CreateSession();
        sessionB.AddMessage(new Message { Role = MessageRole.User, Content = "From beta" });
        await managerB.SaveAsync(sessionB, CancellationToken.None);

        var listA = await managerA.ListSessionsAsync(10, CancellationToken.None);
        var listB = await managerB.ListSessionsAsync(10, CancellationToken.None);

        listA.Should().HaveCount(1);
        listA[0].Id.Should().Be(sessionA.Id);

        listB.Should().HaveCount(1);
        listB[0].Id.Should().Be(sessionB.Id);
    }

    [Fact]
    public async Task ListSessions_HostWorkingDirectoryTakesPrecedenceOverWorkingDirectory()
    {

        var containerManager = new SessionManager(new AppConfig
        {
            DataDirectory = _tempDir,
            WorkingDirectory = "/workspace",
            HostWorkingDirectory = "/Users/dev/myproject"
        });
        var hostManager = new SessionManager(new AppConfig
        {
            DataDirectory = _tempDir,
            WorkingDirectory = "/workspace",
            HostWorkingDirectory = "/Users/dev/other"
        });

        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Docker session" });
        await containerManager.SaveAsync(session, CancellationToken.None);

        var listContainer = await containerManager.ListSessionsAsync(10, CancellationToken.None);
        var listOther = await hostManager.ListSessionsAsync(10, CancellationToken.None);

        listContainer.Should().HaveCount(1);
        listContainer[0].WorkingDirectory.Should().Be("/Users/dev/myproject");
        listOther.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_PopulatesDigestFieldsInIndex()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Refactor the parser" });
        session.AddMessage(new Message { Role = MessageRole.Assistant, Content = "ok" });
        session.TurnCount = 1;

        await _manager.SaveAsync(session, CancellationToken.None);

        var list = await _manager.ListSessionsAsync(10, CancellationToken.None);
        list.Should().HaveCount(1);
        var summary = list[0];
        summary.Title.Should().Be("Refactor the parser");
        summary.MessageCount.Should().Be(3);
        summary.LastActivityAt.Should().BeAfter(default);
        summary.LatestSummary.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_PopulatesLatestSummaryFromCheckpoint()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "do x" });
        session.Checkpoints.Add(new CheckpointEntry
        {
            Id = "c1",
            CreatedAt = DateTime.UtcNow,
            TurnIndex = 1,
            CutoffMessageIndex = 1,
            Summary = "did x",
        });

        await _manager.SaveAsync(session, CancellationToken.None);

        var list = await _manager.ListSessionsAsync(10, CancellationToken.None);
        list[0].LatestSummary.Should().Be("did x");
    }

    [Fact]
    public async Task LoadAsync_PreservesIdAndStartedAt_ForContinuousFile()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "first" });
        await _manager.SaveAsync(session, CancellationToken.None);

        var reloaded = await _manager.LoadAsync(session.Id, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.Id.Should().Be(session.Id);
        reloaded.StartedAt.Should().Be(session.StartedAt);

        // Continuing the resumed session must append to the SAME file, not fork a new one.
        reloaded.AddMessage(new Message { Role = MessageRole.Assistant, Content = "second" });
        await _manager.SaveAsync(reloaded, CancellationToken.None);

        var files = Directory.GetFiles(Path.Combine(_tempDir, "sessions"), "*.jsonl");
        files.Should().HaveCount(1);

        var finalLoad = await _manager.LoadAsync(session.Id, CancellationToken.None);
        finalLoad!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveAndLoad_RestoresModel()
    {
        var session = new SessionState { Model = "claude-opus-4" };
        session.AddMessage(new Message { Role = MessageRole.User, Content = "hi" });
        await _manager.SaveAsync(session, CancellationToken.None);

        var loaded = await _manager.LoadAsync(session.Id, CancellationToken.None);
        loaded!.Model.Should().Be("claude-opus-4");

        var list = await _manager.ListSessionsAsync(10, CancellationToken.None);
        list[0].Model.Should().Be("claude-opus-4");
    }

    [Fact]
    public async Task SaveAndLoad_RestoresSessionLevelState()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "hi" });
        session.TurnCount = 5;
        session.TotalTokensUsed = 1234;
        session.Meta.PlanMode = true;
        session.Todos.Add(new TodoItem { Content = "do x", Status = "in_progress" });

        await _manager.SaveAsync(session, CancellationToken.None);
        var loaded = await _manager.LoadAsync(session.Id, CancellationToken.None);

        loaded!.TurnCount.Should().Be(5);
        loaded.TotalTokensUsed.Should().Be(1234);
        loaded.Meta.PlanMode.Should().BeTrue();
        loaded.Todos.Should().ContainSingle().Which.Content.Should().Be("do x");
    }

    [Fact]
    public async Task DeleteAsync_RemovesSessionFilesAndIndexEntry()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "x" });
        session.Checkpoints.Add(new CheckpointEntry
        {
            Id = "c", CreatedAt = DateTime.UtcNow, TurnIndex = 1, CutoffMessageIndex = 1, Summary = "s",
        });
        await _manager.SaveAsync(session, CancellationToken.None);

        await _manager.DeleteAsync(session.Id, CancellationToken.None);

        (await _manager.LoadAsync(session.Id, CancellationToken.None)).Should().BeNull();
        (await _manager.ListSessionsAsync(10, CancellationToken.None)).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_tempDir, "sessions"), $"*_{session.Id}*").Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_SkipsCorruptMessageLines()
    {
        var sessionsDir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(sessionsDir);
        const string id = "deadbeef1234";
        var path = Path.Combine(sessionsDir, $"2026-01-01_{id}.jsonl");
        var lines = new[]
        {
            "{\"session_id\":\"" + id + "\",\"started_at\":\"2026-01-01T00:00:00Z\",\"working_directory\":\"\"}",
            "{\"role\":\"user\",\"content\":\"hello\"}",
            "{ this is not valid json",
            "{\"role\":\"assistant\",\"content\":\"hi\"}",
        };
        await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n");

        var loaded = await _manager.LoadAsync(id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(2);
        loaded.Messages[0].Content.Should().Be("hello");
        loaded.Messages[1].Content.Should().Be("hi");
    }

    [Fact]
    public async Task LoadAsync_IgnoresCorruptCheckpointsSidecar()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "x" });
        await _manager.SaveAsync(session, CancellationToken.None);

        var jsonl = Directory.GetFiles(Path.Combine(_tempDir, "sessions"), $"*_{session.Id}.jsonl")[0];
        await File.WriteAllTextAsync(jsonl.Replace(".jsonl", ".checkpoints.json"), "{ broken");

        var loaded = await _manager.LoadAsync(session.Id, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(1);
        loaded.Checkpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsEmpty_OnCorruptIndex()
    {
        var sessionsDir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(sessionsDir);
        await File.WriteAllTextAsync(Path.Combine(sessionsDir, "index.json"), "{ not valid json");

        var list = await _manager.ListSessionsAsync(10, CancellationToken.None);

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSessionsAsync_SkipsOrphanedIndexEntries()
    {
        var a = SessionManager.CreateSession();
        a.AddMessage(new Message { Role = MessageRole.User, Content = "a" });
        await _manager.SaveAsync(a, CancellationToken.None);
        var b = SessionManager.CreateSession();
        b.AddMessage(new Message { Role = MessageRole.User, Content = "b" });
        await _manager.SaveAsync(b, CancellationToken.None);

        // Delete b's jsonl directly, leaving its index entry orphaned.
        foreach (var f in Directory.GetFiles(Path.Combine(_tempDir, "sessions"), $"*_{b.Id}.jsonl"))
            File.Delete(f);

        var list = await _manager.ListSessionsAsync(10, CancellationToken.None);

        list.Select(s => s.Id).Should().Contain(a.Id).And.NotContain(b.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
