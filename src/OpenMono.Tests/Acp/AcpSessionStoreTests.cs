using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Session;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpSessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppConfig _cfg;
    private readonly AcpServerSettings _settings;

    public AcpSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openmono-acp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _cfg = new AppConfig { DataDirectory = _tempDir };
        _cfg.Llm.Model = "test-model";
        _settings = new AcpServerSettings { SessionTtlHours = 24 };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Create_assigns_id_model_and_clientTools_and_persists_to_disk()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create(model: "gpt-4o",
            clientTools: new[] { "FileRead", "Bash" }, _cfg);

        session.Id.Should().StartWith("sess_");
        session.Model.Should().Be("gpt-4o");
        session.ClientTools.Should().BeEquivalentTo(new[] { "FileRead", "Bash" });

        var diskFile = Path.Combine(_tempDir, "acp-sessions", session.Id + ".json");
        File.Exists(diskFile).Should().BeTrue();
    }

    [Fact]
    public void Create_uses_cfg_default_model_when_model_arg_null()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create(model: null, clientTools: null, _cfg);

        session.Model.Should().Be("test-model");
        session.ClientTools.Should().BeEmpty();
    }

    [Fact]
    public void Round_trip_persistence_reloads_session_after_store_restart()
    {
        string id;
        DateTime started;
        using (var store = new AcpSessionStore(_cfg, _settings, startReaper: false))
        {
            var session = store.Create("gpt-4o", new[] { "FileRead" }, _cfg);
            session.TurnCount = 3;
            session.PlanMode = true;
            session.Messages.Add(new Message { Role = MessageRole.User, Content = "Hello" });
            session.Todos.Add(new TodoItem { Content = "Refactor auth", Status = "in_progress" });
            store.Save(session);
            id = session.Id;
            started = session.StartedAt;
        }

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var got = reloaded.TryGet(id);

        got.Should().NotBeNull();
        got!.Id.Should().Be(id);
        got.Model.Should().Be("gpt-4o");
        got.ClientTools.Should().BeEquivalentTo(new[] { "FileRead" });
        got.TurnCount.Should().Be(3);
        got.PlanMode.Should().BeTrue();
        got.Messages.Should().HaveCount(1);
        got.Messages[0].Content.Should().Be("Hello");
        got.Todos.Should().HaveCount(1);
        got.Todos[0].Content.Should().Be("Refactor auth");
        got.StartedAt.Should().BeCloseTo(started, TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void TryGet_returns_null_for_unknown_id()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        store.TryGet("sess_doesnotexist").Should().BeNull();
        store.TryGet("not-a-valid-id").Should().BeNull();
        store.TryGet("").Should().BeNull();
    }

    [Fact]
    public void PurgeExpired_deletes_in_memory_and_on_disk()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var session = store.Create("gpt-4o", null, _cfg);
        var path = Path.Combine(_tempDir, "acp-sessions", session.Id + ".json");

        // Simulate inactivity older than 1ms.
        session.LastActivityAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        store.Save(session);

        store.PurgeExpired(TimeSpan.FromMilliseconds(1));

        store.TryGet(session.Id).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void TryGet_returns_null_and_deletes_when_session_is_past_ttl()
    {
        var shortTtl = new AcpServerSettings { SessionTtlHours = 24 }; // ttl positive
        using var store = new AcpSessionStore(_cfg, shortTtl, startReaper: false);

        var session = store.Create("gpt-4o", null, _cfg);
        session.LastActivityAt = DateTime.UtcNow - TimeSpan.FromDays(30);
        store.Save(session);

        var got = store.TryGet(session.Id);
        got.Should().BeNull();

        File.Exists(Path.Combine(_tempDir, "acp-sessions", session.Id + ".json")).Should().BeFalse();
    }

    [Fact]
    public void Save_is_idempotent_and_updates_disk_on_each_call()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create("gpt-4o", null, _cfg);
        session.TurnCount = 1;
        store.Save(session);
        session.TurnCount = 2;
        store.Save(session);

        var path = Path.Combine(_tempDir, "acp-sessions", session.Id + ".json");
        File.Exists(path).Should().BeTrue();

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        reloaded.TryGet(session.Id)!.TurnCount.Should().Be(2);
    }

    [Fact]
    public void Concurrent_Create_produces_unique_ids_and_no_loss()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 100, _ =>
        {
            var s = store.Create(null, null, _cfg);
            ids.Add(s.Id);
        });

        ids.Should().HaveCount(100);
        ids.Distinct().Should().HaveCount(100);

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        foreach (var id in ids)
            reloaded.TryGet(id).Should().NotBeNull("session {0} must round-trip", id);
    }

    [Fact]
    public void Delete_removes_session_from_memory_and_disk()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var session = store.Create("gpt-4o", null, _cfg);
        var path = Path.Combine(_tempDir, "acp-sessions", session.Id + ".json");
        File.Exists(path).Should().BeTrue();

        store.Delete(session.Id);

        store.TryGet(session.Id).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }
}
