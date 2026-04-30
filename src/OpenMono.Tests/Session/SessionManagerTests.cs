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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
