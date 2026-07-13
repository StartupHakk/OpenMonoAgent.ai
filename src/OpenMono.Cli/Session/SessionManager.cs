using System.Text;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Session;

public sealed class SessionManager
{
    private readonly string _sessionDir;
    private readonly string _workingDirectory;

    public SessionManager(AppConfig config)
    {
        _sessionDir = Path.Combine(config.DataDirectory, "sessions");
        _workingDirectory = config.HostWorkingDirectory ?? config.WorkingDirectory;
        Directory.CreateDirectory(_sessionDir);
    }

    public static SessionState CreateSession() => new();

    public async Task SaveAsync(SessionState session, CancellationToken ct)
    {
        var fileName = $"{session.StartedAt:yyyy-MM-dd}_{session.Id}.jsonl";
        var filePath = Path.Combine(_sessionDir, fileName);

        var header = BuildHeader(session);

        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(header, JsonOptions.Default)).Append('\n');
        foreach (var msg in session.Messages)
            sb.Append(JsonSerializer.Serialize(msg, JsonOptions.Default)).Append('\n');

        await WriteAllTextAtomicAsync(filePath, sb.ToString(), ct);

        if (session.Checkpoints.Count > 0)
        {
            var cpPath = Path.Combine(_sessionDir, $"{session.StartedAt:yyyy-MM-dd}_{session.Id}.checkpoints.json");
            await WriteAllTextAtomicAsync(cpPath,
                JsonSerializer.Serialize(session.Checkpoints, JsonOptions.Indented), ct);
        }

        await UpdateIndexAsync(session, ct);
    }

    public async Task AppendMessageAsync(SessionState session, Message message, CancellationToken ct)
    {
        var fileName = $"{session.StartedAt:yyyy-MM-dd}_{session.Id}.jsonl";
        var filePath = Path.Combine(_sessionDir, fileName);

        if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
        {
            var header = BuildHeader(session);
            await File.AppendAllTextAsync(filePath, JsonSerializer.Serialize(header, JsonOptions.Default) + "\n", ct);
        }

        var json = JsonSerializer.Serialize(message, JsonOptions.Default);
        await File.AppendAllTextAsync(filePath, json + "\n", ct);
    }

    public async Task<SessionState?> LoadAsync(string sessionId, CancellationToken ct)
    {

        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var files = Directory.GetFiles(_sessionDir, $"*_{sessionId}.jsonl");
        if (files.Length == 0) return null;

        var lines = await File.ReadAllLinesAsync(files[0], ct);

        var headerLine = lines.FirstOrDefault(l => l.Contains("\"session_id\""));
        SessionHeader? header = headerLine is not null
            ? JsonSerializer.Deserialize<SessionHeader>(headerLine, JsonOptions.Default)
            : null;
        var session = header is not null
            ? new SessionState { Id = header.SessionId, StartedAt = header.StartedAt, Model = header.Model }
            : new SessionState { Id = sessionId };

        if (header is not null)
        {
            session.TurnCount = header.TurnCount;
            session.TotalTokensUsed = header.TotalTokens;
            session.Meta.PlanMode = header.PlanMode;
            if (header.Todos.Count > 0) session.Todos.AddRange(header.Todos);
        }

        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            if (line.Contains("\"session_id\"")) continue;

            try
            {
                var msg = JsonSerializer.Deserialize<Message>(line, JsonOptions.Default);
                if (msg is not null) session.AddMessage(msg);
            }
            catch (JsonException ex)
            {
                OpenMono.Utils.Log.Warn($"Dropped a corrupt session message while loading: {ex.Message}");
            }
        }

        var cpPath = files[0].Replace(".jsonl", ".checkpoints.json");
        if (File.Exists(cpPath))
        {
            try
            {
                var cpJson = await File.ReadAllTextAsync(cpPath, ct);
                var checkpoints = JsonSerializer.Deserialize<List<CheckpointEntry>>(cpJson, JsonOptions.Default) ?? [];
                foreach (var cp in checkpoints)
                    session.Checkpoints.Add(cp);

                if (session.Checkpoints.Count > 0)
                    session.CheckpointCutoffIndex = session.Checkpoints[^1].CutoffMessageIndex;
            }
            catch (JsonException ex)
            {
                OpenMono.Utils.Log.Warn($"Failed to load session checkpoints: {ex.Message}");
            }
        }

        return session;
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return;

        foreach (var f in Directory.GetFiles(_sessionDir, $"*_{sessionId}.jsonl"))
            File.Delete(f);
        foreach (var f in Directory.GetFiles(_sessionDir, $"*_{sessionId}.checkpoints.json"))
            File.Delete(f);

        var indexPath = Path.Combine(_sessionDir, "index.json");
        if (!File.Exists(indexPath)) return;

        var json = await File.ReadAllTextAsync(indexPath, ct);
        var sessions = JsonSerializer.Deserialize<List<SessionSummary>>(json, JsonOptions.Default) ?? [];
        if (sessions.RemoveAll(s => s.Id == sessionId) > 0)
            await WriteAllTextAtomicAsync(indexPath,
                JsonSerializer.Serialize(sessions, JsonOptions.Indented), ct);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct)
    {
        var indexPath = Path.Combine(_sessionDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        List<SessionSummary> sessions;
        try
        {
            var json = await File.ReadAllTextAsync(indexPath, ct);
            sessions = JsonSerializer.Deserialize<List<SessionSummary>>(json, JsonOptions.Default) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        return sessions
            .Where(s => s.WorkingDirectory == _workingDirectory)
            .Where(s => Directory.GetFiles(_sessionDir, $"*_{s.Id}.jsonl").Length > 0)
            .OrderByDescending(s => s.LastActivityAt)
            .ThenByDescending(s => s.StartedAt)
            .Take(limit)
            .ToList();
    }

    private async Task UpdateIndexAsync(SessionState session, CancellationToken ct)
    {
        var indexPath = Path.Combine(_sessionDir, "index.json");
        List<SessionSummary> sessions = [];

        if (File.Exists(indexPath))
        {
            var json = await File.ReadAllTextAsync(indexPath, ct);
            sessions = JsonSerializer.Deserialize<List<SessionSummary>>(json, JsonOptions.Default) ?? [];
        }

        var existing = sessions.FindIndex(s => s.Id == session.Id);
        var firstUser = session.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content;
        var summary = new SessionSummary
        {
            Id = session.Id,
            StartedAt = session.StartedAt,
            LastActivityAt = session.Messages.Count > 0
                ? session.Messages[^1].Timestamp
                : session.StartedAt,
            TurnCount = session.TurnCount,
            TotalTokens = session.TotalTokensUsed,
            WorkingDirectory = _workingDirectory,
            FirstMessage = firstUser is { } c ? c[..Math.Min(100, c.Length)] : "",
            Title = SessionDigest.DeriveTitle(session.Messages),
            Model = session.Model ?? "",
            MessageCount = session.Messages.Count,
            LatestSummary = SessionDigest.DeriveLatestSummary(session.Checkpoints),
        };

        if (existing >= 0)
            sessions[existing] = summary;
        else
            sessions.Add(summary);

        await WriteAllTextAtomicAsync(indexPath,
            JsonSerializer.Serialize(sessions, JsonOptions.Indented), ct);
    }

    private SessionHeader BuildHeader(SessionState session) => new()
    {
        SessionId = session.Id,
        StartedAt = session.StartedAt,
        WorkingDirectory = _workingDirectory,
        Model = session.Model,
        TurnCount = session.TurnCount,
        TotalTokens = session.TotalTokensUsed,
        PlanMode = session.Meta.PlanMode,
        Todos = session.Todos,
    };

    private static async Task WriteAllTextAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record SessionSummary
{
    public required string Id { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public int TurnCount { get; init; }
    public int TotalTokens { get; init; }
    public string WorkingDirectory { get; init; } = "";
    public string FirstMessage { get; init; } = "";
    public string Title { get; init; } = "";
    public string Model { get; init; } = "";
    public int MessageCount { get; init; }
    public string? LatestSummary { get; init; }
}

public sealed record SessionHeader
{
    public required string SessionId { get; init; }
    public required DateTime StartedAt { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Model { get; init; }
    public int TurnCount { get; init; }
    public int TotalTokens { get; init; }
    public bool PlanMode { get; init; }
    public List<TodoItem> Todos { get; init; } = new();
}
