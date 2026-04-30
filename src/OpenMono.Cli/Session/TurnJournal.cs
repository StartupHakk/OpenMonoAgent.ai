using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMono.Config;
using OpenMono.Tools;

namespace OpenMono.Session;

public sealed class TurnJournal : IDisposable
{
    private readonly string _journalPath;
    private readonly object _writeLock = new();
    private readonly List<JournalEvent> _inMemoryEvents = [];
    private StreamWriter? _writer;
    private bool _disposed;

    public string? CurrentTurnId { get; private set; }

    public IReadOnlyList<JournalEvent> Events => _inMemoryEvents;

    public TurnJournal(string journalPath)
    {
        _journalPath = journalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
    }

    public static TurnJournal ForSession(SessionState session, AppConfig config)
    {
        var sessionDir = Path.Combine(config.DataDirectory, "sessions");
        var journalPath = Path.Combine(sessionDir, $"{session.StartedAt:yyyy-MM-dd}_{session.Id}.journal.jsonl");
        return new TurnJournal(journalPath);
    }

    public void StartTurn(int turnNumber, string? parentMessageId, string model)
    {
        CurrentTurnId = $"turn_{turnNumber}_{Guid.NewGuid():N}"[..20];
        Append(new TurnStarted
        {
            TurnId = CurrentTurnId,
            ParentMessageId = parentMessageId,
            Model = model,
            Timestamp = DateTime.UtcNow
        });
    }

    public void FinishTurn(string finishReason)
    {
        if (CurrentTurnId is not null)
        {
            Append(new TurnFinished
            {
                TurnId = CurrentTurnId,
                FinishReason = finishReason,
                Timestamp = DateTime.UtcNow
            });
            CurrentTurnId = null;
        }
    }

    public void RecordToolCallReceived(string callId, string toolName, string arguments)
    {
        var argsHash = HashArguments(arguments);
        Append(new ToolCallReceived
        {
            TurnId = CurrentTurnId ?? "unknown",
            CallId = callId,
            ToolName = toolName,
            ArgsHash = argsHash,
            Timestamp = DateTime.UtcNow
        });
    }

    public void RecordSchemaValidated(string callId)
    {
        Append(new SchemaValidated { CallId = callId, Timestamp = DateTime.UtcNow });
    }

    public void RecordSchemaRejected(string callId, string error)
    {
        Append(new SchemaRejected { CallId = callId, Error = error, Timestamp = DateTime.UtcNow });
    }

    public void RecordSanityChecked(string callId)
    {
        Append(new SanityChecked { CallId = callId, Timestamp = DateTime.UtcNow });
    }

    public void RecordSanityRejected(string callId, string reason)
    {
        Append(new SanityRejected { CallId = callId, Reason = reason, Timestamp = DateTime.UtcNow });
    }

    public void RecordPermissionDecided(string callId, bool allowed, string? reason = null)
    {
        Append(new PermissionDecided
        {
            CallId = callId,
            Decision = allowed ? "allow" : "deny",
            Reason = reason,
            Timestamp = DateTime.UtcNow
        });
    }

    public void RecordToolStarted(string callId)
    {
        Append(new ToolStarted { CallId = callId, Timestamp = DateTime.UtcNow });
    }

    public void RecordToolCompleted(string callId, ResultClass resultClass, IReadOnlyList<string>? artifactIds = null)
    {
        Append(new ToolCompleted
        {
            CallId = callId,
            ResultClass = resultClass.ToString(),
            ArtifactIds = artifactIds ?? [],
            Timestamp = DateTime.UtcNow
        });
    }

    public void RecordToolCrashed(string callId, string exceptionClass, string message)
    {
        Append(new ToolCrashed
        {
            CallId = callId,
            ExceptionClass = exceptionClass,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    public void Append(JournalEvent evt)
    {
        lock (_writeLock)
        {
            _inMemoryEvents.Add(evt);

            _writer ??= new StreamWriter(_journalPath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };

            var json = JsonSerializer.Serialize(evt, JournalSerializerContext.Default.JournalEvent);
            _writer.WriteLine(json);
        }
    }

    public static async Task<IReadOnlyList<JournalEvent>> LoadAsync(string journalPath, CancellationToken ct)
    {
        if (!File.Exists(journalPath))
            return [];

        var events = new List<JournalEvent>();
        var lines = await File.ReadAllLinesAsync(journalPath, ct);

        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                var evt = JsonSerializer.Deserialize(line, JournalSerializerContext.Default.JournalEvent);
                if (evt is not null)
                    events.Add(evt);
            }
            catch (JsonException)
            {

            }
        }

        return events;
    }

    public static IReadOnlyList<string> FindIncompleteToolCalls(IReadOnlyList<JournalEvent> events)
    {
        var started = new HashSet<string>();
        var completed = new HashSet<string>();

        foreach (var evt in events)
        {
            switch (evt)
            {
                case ToolStarted ts:
                    started.Add(ts.CallId);
                    break;
                case ToolCompleted tc:
                    completed.Add(tc.CallId);
                    break;
                case ToolCrashed tcr:
                    completed.Add(tcr.CallId);
                    break;
            }
        }

        return started.Except(completed).ToList();
    }

    private static string HashArguments(string arguments)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(arguments));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TurnStarted), "turn_started")]
[JsonDerivedType(typeof(TurnFinished), "turn_finished")]
[JsonDerivedType(typeof(ToolCallReceived), "tool_call_received")]
[JsonDerivedType(typeof(SchemaValidated), "schema_validated")]
[JsonDerivedType(typeof(SchemaRejected), "schema_rejected")]
[JsonDerivedType(typeof(SanityChecked), "sanity_checked")]
[JsonDerivedType(typeof(SanityRejected), "sanity_rejected")]
[JsonDerivedType(typeof(PermissionDecided), "permission_decided")]
[JsonDerivedType(typeof(ToolStarted), "tool_started")]
[JsonDerivedType(typeof(ToolCompleted), "tool_completed")]
[JsonDerivedType(typeof(ToolCrashed), "tool_crashed")]
public abstract record JournalEvent
{
    public required DateTime Timestamp { get; init; }
}

public sealed record TurnStarted : JournalEvent
{
    public required string TurnId { get; init; }
    public string? ParentMessageId { get; init; }
    public required string Model { get; init; }
}

public sealed record TurnFinished : JournalEvent
{
    public required string TurnId { get; init; }
    public required string FinishReason { get; init; }
}

public sealed record ToolCallReceived : JournalEvent
{
    public required string TurnId { get; init; }
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgsHash { get; init; }
}

public sealed record SchemaValidated : JournalEvent
{
    public required string CallId { get; init; }
}

public sealed record SchemaRejected : JournalEvent
{
    public required string CallId { get; init; }
    public required string Error { get; init; }
}

public sealed record SanityChecked : JournalEvent
{
    public required string CallId { get; init; }
}

public sealed record SanityRejected : JournalEvent
{
    public required string CallId { get; init; }
    public required string Reason { get; init; }
}

public sealed record PermissionDecided : JournalEvent
{
    public required string CallId { get; init; }
    public required string Decision { get; init; }
    public string? Reason { get; init; }
}

public sealed record ToolStarted : JournalEvent
{
    public required string CallId { get; init; }
}

public sealed record ToolCompleted : JournalEvent
{
    public required string CallId { get; init; }
    public required string ResultClass { get; init; }
    public IReadOnlyList<string> ArtifactIds { get; init; } = [];
}

public sealed record ToolCrashed : JournalEvent
{
    public required string CallId { get; init; }
    public required string ExceptionClass { get; init; }
    public required string Message { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalEvent))]
[JsonSerializable(typeof(TurnStarted))]
[JsonSerializable(typeof(TurnFinished))]
[JsonSerializable(typeof(ToolCallReceived))]
[JsonSerializable(typeof(SchemaValidated))]
[JsonSerializable(typeof(SchemaRejected))]
[JsonSerializable(typeof(SanityChecked))]
[JsonSerializable(typeof(SanityRejected))]
[JsonSerializable(typeof(PermissionDecided))]
[JsonSerializable(typeof(ToolStarted))]
[JsonSerializable(typeof(ToolCompleted))]
[JsonSerializable(typeof(ToolCrashed))]
internal partial class JournalSerializerContext : JsonSerializerContext { }
