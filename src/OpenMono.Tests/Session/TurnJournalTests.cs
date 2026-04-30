using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Session;

public class TurnJournalTests : IDisposable
{
    private readonly string _tempDir;

    public TurnJournalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-journal-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void StartTurn_RecordsTurnStartedEvent()
    {
        var journalPath = Path.Combine(_tempDir, "test1.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.StartTurn(1, "parent_msg_123", "gpt-4");

        journal.Events.Should().HaveCount(1);
        var evt = journal.Events[0] as TurnStarted;
        evt.Should().NotBeNull();
        evt!.TurnId.Should().StartWith("turn_1_");
        evt.ParentMessageId.Should().Be("parent_msg_123");
        evt.Model.Should().Be("gpt-4");
    }

    [Fact]
    public void FinishTurn_RecordsTurnFinishedEvent()
    {
        var journalPath = Path.Combine(_tempDir, "test2.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.StartTurn(1, null, "model-3");
        journal.FinishTurn("text_only");

        journal.Events.Should().HaveCount(2);
        var evt = journal.Events[1] as TurnFinished;
        evt.Should().NotBeNull();
        evt!.FinishReason.Should().Be("text_only");
    }

    [Fact]
    public void RecordToolCallReceived_CapturesToolAndArgsHash()
    {
        var journalPath = Path.Combine(_tempDir, "test3.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.StartTurn(1, null, "model");
        journal.RecordToolCallReceived("call_123", "FileRead", """{"file_path": "/test.txt"}""");

        var evt = journal.Events.OfType<ToolCallReceived>().Single();
        evt.CallId.Should().Be("call_123");
        evt.ToolName.Should().Be("FileRead");
        evt.ArgsHash.Should().HaveLength(16);
    }

    [Fact]
    public void FullToolLifecycle_RecordsAllEvents()
    {
        var journalPath = Path.Combine(_tempDir, "test4.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.StartTurn(1, null, "model");
        journal.RecordToolCallReceived("call_1", "Bash", """{"command": "ls"}""");
        journal.RecordSchemaValidated("call_1");
        journal.RecordSanityChecked("call_1");
        journal.RecordPermissionDecided("call_1", true);
        journal.RecordToolStarted("call_1");
        journal.RecordToolCompleted("call_1", ResultClass.Success);
        journal.FinishTurn("completed");

        journal.Events.Should().HaveCount(8);
        journal.Events[0].Should().BeOfType<TurnStarted>();
        journal.Events[1].Should().BeOfType<ToolCallReceived>();
        journal.Events[2].Should().BeOfType<SchemaValidated>();
        journal.Events[3].Should().BeOfType<SanityChecked>();
        journal.Events[4].Should().BeOfType<PermissionDecided>();
        journal.Events[5].Should().BeOfType<ToolStarted>();
        journal.Events[6].Should().BeOfType<ToolCompleted>();
        journal.Events[7].Should().BeOfType<TurnFinished>();
    }

    [Fact]
    public async Task EventsPersistedToFile_CanBeReloaded()
    {
        var journalPath = Path.Combine(_tempDir, "persist.journal.jsonl");

        using (var journal = new TurnJournal(journalPath))
        {
            journal.StartTurn(1, null, "model-3");
            journal.RecordToolCallReceived("call_1", "FileRead", "{}");
            journal.RecordToolStarted("call_1");
            journal.RecordToolCompleted("call_1", ResultClass.Success);
            journal.FinishTurn("completed");
        }

        var events = await TurnJournal.LoadAsync(journalPath, CancellationToken.None);

        events.Should().HaveCount(5);
        events[0].Should().BeOfType<TurnStarted>();
        events[1].Should().BeOfType<ToolCallReceived>();
        events[2].Should().BeOfType<ToolStarted>();
        events[3].Should().BeOfType<ToolCompleted>();
        events[4].Should().BeOfType<TurnFinished>();
    }

    [Fact]
    public async Task LoadAsync_NonExistentFile_ReturnsEmpty()
    {
        var events = await TurnJournal.LoadAsync("/nonexistent/path.jsonl", CancellationToken.None);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_MalformedLines_SkipsAndContinues()
    {
        var journalPath = Path.Combine(_tempDir, "malformed.journal.jsonl");

        await File.WriteAllTextAsync(journalPath, """
            {"type":"turn_started","turn_id":"t1","model":"m","timestamp":"2024-01-01T00:00:00Z"}
            not valid json
            {"type":"tool_started","call_id":"c1","timestamp":"2024-01-01T00:00:01Z"}
            """);

        var events = await TurnJournal.LoadAsync(journalPath, CancellationToken.None);

        events.Should().HaveCount(2);
    }

    [Fact]
    public void FindIncompleteToolCalls_IdentifiesStartedButNotCompleted()
    {
        var events = new List<JournalEvent>
        {
            new ToolStarted { CallId = "call_1", Timestamp = DateTime.UtcNow },
            new ToolCompleted { CallId = "call_1", ResultClass = "Success", Timestamp = DateTime.UtcNow },
            new ToolStarted { CallId = "call_2", Timestamp = DateTime.UtcNow },

        };

        var incomplete = TurnJournal.FindIncompleteToolCalls(events);

        incomplete.Should().ContainSingle().Which.Should().Be("call_2");
    }

    [Fact]
    public void FindIncompleteToolCalls_CrashedToolsAreComplete()
    {
        var events = new List<JournalEvent>
        {
            new ToolStarted { CallId = "call_1", Timestamp = DateTime.UtcNow },
            new ToolCrashed { CallId = "call_1", ExceptionClass = "Exception", Message = "error", Timestamp = DateTime.UtcNow },
        };

        var incomplete = TurnJournal.FindIncompleteToolCalls(events);

        incomplete.Should().BeEmpty();
    }

    [Fact]
    public void FindIncompleteToolCalls_EmptyEvents_ReturnsEmpty()
    {
        var incomplete = TurnJournal.FindIncompleteToolCalls([]);
        incomplete.Should().BeEmpty();
    }

    [Fact]
    public void FindIncompleteToolCalls_MultipleIncomplete()
    {
        var events = new List<JournalEvent>
        {
            new ToolStarted { CallId = "a", Timestamp = DateTime.UtcNow },
            new ToolStarted { CallId = "b", Timestamp = DateTime.UtcNow },
            new ToolStarted { CallId = "c", Timestamp = DateTime.UtcNow },
            new ToolCompleted { CallId = "b", ResultClass = "Success", Timestamp = DateTime.UtcNow },
        };

        var incomplete = TurnJournal.FindIncompleteToolCalls(events);

        incomplete.Should().HaveCount(2);
        incomplete.Should().Contain("a");
        incomplete.Should().Contain("c");
    }

    [Fact]
    public void RecordSchemaRejected_CapturesError()
    {
        var journalPath = Path.Combine(_tempDir, "schema_error.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.RecordSchemaRejected("call_1", "missing required field 'file_path'");

        var evt = journal.Events.OfType<SchemaRejected>().Single();
        evt.CallId.Should().Be("call_1");
        evt.Error.Should().Contain("missing required field");
    }

    [Fact]
    public void RecordSanityRejected_CapturesReason()
    {
        var journalPath = Path.Combine(_tempDir, "sanity_error.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.RecordSanityRejected("call_1", "write to /etc/passwd denied");

        var evt = journal.Events.OfType<SanityRejected>().Single();
        evt.Reason.Should().Contain("/etc/passwd");
    }

    [Fact]
    public void RecordToolCrashed_CapturesExceptionDetails()
    {
        var journalPath = Path.Combine(_tempDir, "crash.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.RecordToolCrashed("call_1", "NullReferenceException", "Object reference not set");

        var evt = journal.Events.OfType<ToolCrashed>().Single();
        evt.ExceptionClass.Should().Be("NullReferenceException");
        evt.Message.Should().Contain("Object reference");
    }

    [Fact]
    public void RecordPermissionDecided_CapturesDenyWithReason()
    {
        var journalPath = Path.Combine(_tempDir, "permission.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.RecordPermissionDecided("call_1", false, "User denied");

        var evt = journal.Events.OfType<PermissionDecided>().Single();
        evt.Decision.Should().Be("deny");
        evt.Reason.Should().Be("User denied");
    }

    [Fact]
    public void FinishTurn_WithoutStartTurn_DoesNotThrow()
    {
        var journalPath = Path.Combine(_tempDir, "no_start.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.FinishTurn("orphan");

        journal.Events.Should().BeEmpty();
    }

    [Fact]
    public void ArgsHash_IsDeterministic()
    {
        var journalPath = Path.Combine(_tempDir, "hash.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        var args = """{"file_path": "/test.txt", "limit": 100}""";
        journal.RecordToolCallReceived("call_1", "FileRead", args);
        journal.RecordToolCallReceived("call_2", "FileRead", args);

        var evt1 = journal.Events.OfType<ToolCallReceived>().First();
        var evt2 = journal.Events.OfType<ToolCallReceived>().Last();

        evt1.ArgsHash.Should().Be(evt2.ArgsHash);
    }

    [Fact]
    public void RecordToolCompleted_WithArtifacts()
    {
        var journalPath = Path.Combine(_tempDir, "artifacts.journal.jsonl");
        using var journal = new TurnJournal(journalPath);

        journal.RecordToolCompleted("call_1", ResultClass.Success, ["art_001", "art_002"]);

        var evt = journal.Events.OfType<ToolCompleted>().Single();
        evt.ArtifactIds.Should().HaveCount(2);
        evt.ArtifactIds.Should().Contain("art_001");
    }

    [Fact]
    public void Timestamps_AreRecorded()
    {
        var journalPath = Path.Combine(_tempDir, "timestamps.journal.jsonl");
        var before = DateTime.UtcNow;

        using var journal = new TurnJournal(journalPath);
        journal.StartTurn(1, null, "model");

        var after = DateTime.UtcNow;
        var evt = journal.Events[0] as TurnStarted;

        evt!.Timestamp.Should().BeOnOrAfter(before);
        evt.Timestamp.Should().BeOnOrBefore(after);
    }

    [Fact]
    public async Task AllEventTypes_SerializeAndDeserializeCorrectly()
    {
        var journalPath = Path.Combine(_tempDir, "roundtrip.journal.jsonl");

        using (var journal = new TurnJournal(journalPath))
        {
            journal.StartTurn(1, "parent", "model");
            journal.RecordToolCallReceived("c1", "Tool", "{}");
            journal.RecordSchemaValidated("c1");
            journal.RecordSchemaRejected("c2", "error");
            journal.RecordSanityChecked("c1");
            journal.RecordSanityRejected("c3", "reason");
            journal.RecordPermissionDecided("c1", true);
            journal.RecordPermissionDecided("c4", false, "denied");
            journal.RecordToolStarted("c1");
            journal.RecordToolCompleted("c1", ResultClass.Success, ["a1"]);
            journal.RecordToolCrashed("c5", "Ex", "msg");
            journal.FinishTurn("done");
        }

        var events = await TurnJournal.LoadAsync(journalPath, CancellationToken.None);

        events.Should().HaveCount(12);

        events[0].Should().BeOfType<TurnStarted>();
        events[1].Should().BeOfType<ToolCallReceived>();
        events[2].Should().BeOfType<SchemaValidated>();
        events[3].Should().BeOfType<SchemaRejected>();
        events[4].Should().BeOfType<SanityChecked>();
        events[5].Should().BeOfType<SanityRejected>();
        events[6].Should().BeOfType<PermissionDecided>();
        events[7].Should().BeOfType<PermissionDecided>();
        events[8].Should().BeOfType<ToolStarted>();
        events[9].Should().BeOfType<ToolCompleted>();
        events[10].Should().BeOfType<ToolCrashed>();
        events[11].Should().BeOfType<TurnFinished>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
