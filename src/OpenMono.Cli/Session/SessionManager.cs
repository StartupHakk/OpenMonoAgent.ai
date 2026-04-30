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

        await using var writer = new StreamWriter(filePath, append: false);

        var header = new SessionHeader
        {
            SessionId = session.Id,
            StartedAt = session.StartedAt,
            WorkingDirectory = _workingDirectory,
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOptions.Default).AsMemory(), ct);

        foreach (var msg in session.Messages)
        {
            var json = JsonSerializer.Serialize(msg, JsonOptions.Default);
            await writer.WriteLineAsync(json.AsMemory(), ct);
        }

        if (session.Checkpoints.Count > 0)
        {
            var cpPath = Path.Combine(_sessionDir, $"{session.StartedAt:yyyy-MM-dd}_{session.Id}.checkpoints.json");
            await File.WriteAllTextAsync(cpPath,
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
            var header = new SessionHeader
            {
                SessionId = session.Id,
                StartedAt = session.StartedAt,
                WorkingDirectory = _workingDirectory,
            };
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

        var session = new SessionState();
        var lines = await File.ReadAllLinesAsync(files[0], ct);

        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {

            if (line.Contains("\"session_id\"")) continue;

            var msg = JsonSerializer.Deserialize<Message>(line, JsonOptions.Default);
            if (msg is not null) session.AddMessage(msg);
        }

        var cpPath = files[0].Replace(".jsonl", ".checkpoints.json");
        if (File.Exists(cpPath))
        {
            var cpJson = await File.ReadAllTextAsync(cpPath, ct);
            var checkpoints = JsonSerializer.Deserialize<List<CheckpointEntry>>(cpJson, JsonOptions.Default) ?? [];
            foreach (var cp in checkpoints)
                session.Checkpoints.Add(cp);

            if (session.Checkpoints.Count > 0)
                session.CheckpointCutoffIndex = session.Checkpoints[^1].CutoffMessageIndex;
        }

        return session;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct)
    {
        var indexPath = Path.Combine(_sessionDir, "index.json");
        if (!File.Exists(indexPath)) return [];

        var json = await File.ReadAllTextAsync(indexPath, ct);
        var sessions = JsonSerializer.Deserialize<List<SessionSummary>>(json, JsonOptions.Default) ?? [];

        return sessions
            .Where(s => s.WorkingDirectory == _workingDirectory)
            .OrderByDescending(s => s.StartedAt)
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
        var summary = new SessionSummary
        {
            Id = session.Id,
            StartedAt = session.StartedAt,
            TurnCount = session.TurnCount,
            TotalTokens = session.TotalTokensUsed,
            WorkingDirectory = _workingDirectory,
            FirstMessage = session.Messages
                .FirstOrDefault(m => m.Role == MessageRole.User)?.Content?[..Math.Min(100,
                    session.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content?.Length ?? 0)] ?? "",
        };

        if (existing >= 0)
            sessions[existing] = summary;
        else
            sessions.Add(summary);

        await File.WriteAllTextAsync(indexPath,
            JsonSerializer.Serialize(sessions, JsonOptions.Indented), ct);
    }
}

public sealed record SessionSummary
{
    public required string Id { get; init; }
    public required DateTime StartedAt { get; init; }
    public int TurnCount { get; init; }
    public int TotalTokens { get; init; }
    public string WorkingDirectory { get; init; } = "";
    public string FirstMessage { get; init; } = "";
}

public sealed record SessionHeader
{
    public required string SessionId { get; init; }
    public required DateTime StartedAt { get; init; }
    public required string WorkingDirectory { get; init; }
}
