using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Session;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpSessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _legacyDir;
    private readonly string _sessionsDir;
    private readonly AppConfig _cfg;
    private readonly AcpServerSettings _settings;

    public AcpSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openmono-acp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        // legacy blobs (pre-unification) live here; the unified store writes under sessions/.
        _legacyDir = Path.Combine(_tempDir, "acp-sessions");
        _sessionsDir = Path.Combine(_tempDir, "sessions");
        _cfg = new AppConfig { DataDirectory = _tempDir };
        _cfg.Llm.Model = "test-model";

        // TTL > 0 keeps the existing expiry tests meaningful; never-expire is tested separately.
        _settings = new AcpServerSettings { SessionTtlHours = 24, SessionsDirectory = _legacyDir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch {  }
    }

    [Fact]
    public void Create_assigns_id_and_model_and_persists_to_disk()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create(model: "gpt-4o", _cfg);

        session.Id.Should().StartWith("sess_");
        session.Model.Should().Be("gpt-4o");

        Directory.GetFiles(_sessionsDir, $"*_{session.Id}.jsonl").Should().HaveCount(1);
    }

    [Fact]
    public void Create_uses_cfg_default_model_when_model_arg_null()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create(model: null, _cfg);

        session.Model.Should().Be("test-model");
    }

    [Fact]
    public void Directory_is_the_unified_sessions_directory()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);
        store.Directory.Should().Be(_sessionsDir);
        Directory.Exists(store.Directory).Should().BeTrue();
    }

    [Fact]
    public void Round_trip_persistence_reloads_session_after_store_restart()
    {
        string id;
        DateTime started;
        using (var store = new AcpSessionStore(_cfg, _settings, startReaper: false))
        {
            var session = store.Create("gpt-4o", _cfg);
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
    public void Never_expires_when_ttl_is_zero()
    {
        var neverExpire = new AcpServerSettings { SessionTtlHours = 0, SessionsDirectory = _legacyDir };
        using var store = new AcpSessionStore(_cfg, neverExpire, startReaper: false);

        var session = store.Create("gpt-4o", _cfg);
        session.LastActivityAt = DateTime.UtcNow - TimeSpan.FromDays(365);
        store.Save(session);

        store.TryGet(session.Id).Should().NotBeNull("TTL=0 means sessions never expire");
        store.PurgeExpired(Timeout.InfiniteTimeSpan);
        store.TryGet(session.Id).Should().NotBeNull();
    }

    [Fact]
    public void PurgeExpired_deletes_live_session()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var session = store.Create("gpt-4o", _cfg);

        session.LastActivityAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        store.Save(session);

        store.PurgeExpired(TimeSpan.FromMilliseconds(1));

        store.TryGet(session.Id).Should().BeNull();
        Directory.GetFiles(_sessionsDir, $"*_{session.Id}.jsonl").Should().BeEmpty();
    }

    [Fact]
    public void TryGet_returns_null_and_deletes_when_live_session_past_ttl()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create("gpt-4o", _cfg);
        session.LastActivityAt = DateTime.UtcNow - TimeSpan.FromDays(30);
        store.Save(session);

        store.TryGet(session.Id).Should().BeNull();
        Directory.GetFiles(_sessionsDir, $"*_{session.Id}.jsonl").Should().BeEmpty();
    }

    [Fact]
    public void Save_is_idempotent_and_updates_disk_on_each_call()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var session = store.Create("gpt-4o", _cfg);
        session.TurnCount = 1;
        store.Save(session);
        session.TurnCount = 2;
        store.Save(session);

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        reloaded.TryGet(session.Id)!.TurnCount.Should().Be(2);
    }

    [Fact]
    public void Concurrent_Create_produces_unique_ids_and_no_loss()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 100, _ => ids.Add(store.Create(null, _cfg).Id));

        ids.Should().HaveCount(100);
        ids.Distinct().Should().HaveCount(100);

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        foreach (var id in ids)
            reloaded.TryGet(id).Should().NotBeNull("session {0} must round-trip", id);
    }

    [Fact]
    public async Task Concurrent_Save_on_same_session_does_not_corrupt_disk()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var session = store.Create("gpt-4o", _cfg);
        session.Messages.Add(new Message { Role = MessageRole.User, Content = "hi" });

        var t1 = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                session.TurnCount = i;
                store.Save(session);
            }
        });
        var t2 = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                session.LastActivityAt = DateTime.UtcNow;
                store.Save(session);
            }
        });
        await Task.WhenAll(t1, t2);

        Directory.GetFiles(_sessionsDir, $"*_{session.Id}.jsonl").Should().HaveCount(1);

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        reloaded.TryGet(session.Id).Should().NotBeNull();
    }

    [Fact]
    public void Round_trip_preserves_assistant_tool_calls_and_tool_results()
    {
        string id;
        using (var store = new AcpSessionStore(_cfg, _settings, startReaper: false))
        {
            var session = store.Create("gpt-4o", _cfg);
            session.Messages.Add(new Message { Role = MessageRole.User, Content = "delete /tmp/x" });
            session.Messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = "Sure.",
                ToolCalls = new()
                {
                    new ToolCall { Id = "call_1", Name = "Bash", Arguments = "{\"command\":\"rm /tmp/x\"}" },
                },
            });
            session.Messages.Add(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = "call_1",
                ToolName = "Bash",
                Content = "exit:0",
            });
            store.Save(session);
            id = session.Id;
        }

        using var reloaded = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var got = reloaded.TryGet(id)!;

        got.Messages.Should().HaveCount(3);

        var assistant = got.Messages[1];
        assistant.Role.Should().Be(MessageRole.Assistant);
        assistant.ToolCalls.Should().NotBeNull().And.HaveCount(1);
        assistant.ToolCalls![0].Id.Should().Be("call_1");
        assistant.ToolCalls[0].Name.Should().Be("Bash");
        assistant.ToolCalls[0].Arguments.Should().Be("{\"command\":\"rm /tmp/x\"}");

        var toolMsg = got.Messages[2];
        toolMsg.Role.Should().Be(MessageRole.Tool);
        toolMsg.ToolCallId.Should().Be("call_1");
        toolMsg.ToolName.Should().Be("Bash");
        toolMsg.Content.Should().Be("exit:0");
    }

    [Fact]
    public void Delete_removes_session_from_memory_and_disk()
    {
        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);
        var session = store.Create("gpt-4o", _cfg);
        Directory.GetFiles(_sessionsDir, $"*_{session.Id}.jsonl").Should().HaveCount(1);

        store.Delete(session.Id);

        store.TryGet(session.Id).Should().BeNull();
        Directory.GetFiles(_sessionsDir, $"*_{session.Id}.jsonl").Should().BeEmpty();
    }

    [Fact]
    public void Migrates_legacy_blob_into_unified_format_on_construction()
    {
        Directory.CreateDirectory(_legacyDir);
        var legacyPath = Path.Combine(_legacyDir, "sess_deadbeef.json");
        File.WriteAllText(legacyPath, """
            {"Id":"sess_deadbeef","StartedAt":"2026-01-01T00:00:00Z","Model":"gpt-4o",
             "TurnCount":2,"PlanMode":true,
             "Messages":[{"Role":"user","Content":"hello legacy"}]}
            """);

        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        var got = store.TryGet("sess_deadbeef");
        got.Should().NotBeNull();
        got!.Model.Should().Be("gpt-4o");
        got.TurnCount.Should().Be(2);
        got.PlanMode.Should().BeTrue();
        got.Messages.Should().ContainSingle().Which.Content.Should().Be("hello legacy");

        File.Exists(legacyPath).Should().BeFalse("the original blob is moved aside after migration");
        File.Exists(Path.Combine(_legacyDir, "migrated", "sess_deadbeef.json")).Should().BeTrue();
    }

    [Fact]
    public void Migration_quarantines_unparseable_legacy_blob()
    {
        Directory.CreateDirectory(_legacyDir);
        var bogusPath = Path.Combine(_legacyDir, "sess_garbage.json");
        File.WriteAllText(bogusPath, "{ this is not valid JSON");

        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        File.Exists(bogusPath).Should().BeFalse("the corrupt file must be renamed");
        File.Exists(bogusPath + ".corrupt").Should().BeTrue("renamed sidecar should appear");
        store.TryGet("sess_garbage").Should().BeNull();
    }

    [Fact]
    public void Migration_quarantines_blob_with_invalid_id()
    {
        Directory.CreateDirectory(_legacyDir);
        var p = Path.Combine(_legacyDir, "sess_badid.json");
        File.WriteAllText(p, "{\"Id\":\"sess_zzz!!!\",\"StartedAt\":\"2026-01-01T00:00:00Z\",\"Messages\":[]}");

        using var store = new AcpSessionStore(_cfg, _settings, startReaper: false);

        File.Exists(p).Should().BeFalse("a blob with an invalid id must be quarantined, not migrated");
        File.Exists(p + ".corrupt").Should().BeTrue();
        Directory.GetFiles(_sessionsDir, "*.jsonl").Should().BeEmpty("nothing should be migrated for an invalid id");
    }

    [Fact]
    public void Migration_skips_corrupt_files_without_throwing()
    {
        Directory.CreateDirectory(_legacyDir);
        File.WriteAllText(Path.Combine(_legacyDir, "sess_bad1.json"), "not json");
        File.WriteAllText(Path.Combine(_legacyDir, "sess_bad2.json"), "{ \"id\": broken }");

        Action ctor = () => { using var _ = new AcpSessionStore(_cfg, _settings, startReaper: false); };
        ctor.Should().NotThrow();
    }

    [Fact]
    public async Task RegisterPause_and_TryResolvePause_complete_the_TCS_with_response()
    {
        var session = NewBareSession();
        var tcs = session.RegisterPause("perm_abc", PendingResponseKind.Permission, "Bash|rm /tmp/x");

        session.TryResolvePause("perm_abc", new AcpPermissionResponse(Allow: true)).Should().BeTrue();
        tcs.Task.IsCompletedSuccessfully.Should().BeTrue();

        var response = await tcs.Task;
        response.Should().BeOfType<AcpPermissionResponse>().Which.Allow.Should().BeTrue();
    }

    [Fact]
    public void RegisterPause_with_duplicate_id_throws()
    {
        var session = NewBareSession();
        session.RegisterPause("perm_abc", PendingResponseKind.Permission, "Bash|rm /tmp/x");

        Action act = () => session.RegisterPause("perm_abc", PendingResponseKind.Permission, "Bash|something else");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate pause id*");
    }

    [Fact]
    public void TryResolvePause_returns_false_for_unknown_id()
    {
        var session = NewBareSession();
        session.TryResolvePause("nope", new AcpCancelledResponse()).Should().BeFalse();
    }

    [Fact]
    public void CancelAllPending_cancels_outstanding_TCS_and_clears_registry()
    {
        var session = NewBareSession();
        var tcs1 = session.RegisterPause("perm_1", PendingResponseKind.Permission, "Bash|x");
        var tcs2 = session.RegisterPause("ask_1", PendingResponseKind.UserInput, "Which algorithm?");
        session.PendingIds.Should().BeEquivalentTo("perm_1", "ask_1");

        session.CancelAllPending();

        tcs1.Task.IsCanceled.Should().BeTrue();
        tcs2.Task.IsCanceled.Should().BeTrue();
        session.PendingIds.Should().BeEmpty();
    }

    [Fact]
    public void LookupPauseContext_returns_kind_and_contextKey_for_registered_pauses()
    {
        var session = NewBareSession();
        session.RegisterPause("perm_xyz", PendingResponseKind.Permission, "Bash|rm node_modules");
        session.RegisterPause("ask_xyz", PendingResponseKind.UserInput, "Which encryption?");

        session.LookupPauseContext("perm_xyz").Should().Be((PendingResponseKind.Permission, "Bash|rm node_modules"));
        session.LookupPauseContext("ask_xyz").Should().Be((PendingResponseKind.UserInput, "Which encryption?"));
        session.LookupPauseContext("missing").Should().BeNull();
    }

    [Fact]
    public void RememberPermission_and_TryGetRememberedPermission_round_trip()
    {
        var session = NewBareSession();
        session.TryGetRememberedPermission("Bash|x").Should().BeNull();

        session.RememberPermission("Bash|x", allow: true);
        session.RememberPermission("Bash|y", allow: false);

        session.TryGetRememberedPermission("Bash|x").Should().BeTrue();
        session.TryGetRememberedPermission("Bash|y").Should().BeFalse();
        session.TryGetRememberedPermission("Bash|never-asked").Should().BeNull();
    }

    [Fact]
    public void RememberUserInput_and_TryGetRememberedUserInput_round_trip()
    {
        var session = NewBareSession();
        session.RememberUserInput("which algorithm?", "AES-256-GCM");
        session.TryGetRememberedUserInput("which algorithm?").Should().Be("AES-256-GCM");
        session.TryGetRememberedUserInput("never-asked").Should().BeNull();
    }

    private static AcpSession NewBareSession() => new()
    {
        State = new SessionState
        {
            Id = "sess_test",
            StartedAt = DateTime.UtcNow,
            Model = "test-model",
        },
    };
}
