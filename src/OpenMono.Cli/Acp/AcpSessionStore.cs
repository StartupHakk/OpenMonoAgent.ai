using System.Collections.Concurrent;
using System.Text.Json;
using OpenMono.Config;
using OpenMono.Session;

namespace OpenMono.Acp;

public sealed class AcpSessionStore : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, AcpSession> _live = new();
    private readonly TimeSpan _ttl;
    private readonly Timer? _reaper;
    private readonly object _ioLock = new();
    private bool _disposed;

    public AcpSessionStore(AppConfig cfg, AcpServerSettings settings, bool startReaper = true)
    {
        _sessions = new SessionManager(cfg);
        _dir = Path.Combine(cfg.DataDirectory, "sessions");
        System.IO.Directory.CreateDirectory(_dir);
        _ttl = settings.SessionTtlHours > 0
            ? TimeSpan.FromHours(settings.SessionTtlHours)
            : Timeout.InfiniteTimeSpan;

        MigrateLegacyBlobs(cfg, settings);

        if (startReaper && _ttl != Timeout.InfiniteTimeSpan)
        {
            var period = TimeSpan.FromMinutes(5);
            _reaper = new Timer(_ => PurgeExpired(_ttl), null, period, period);
        }
    }

    public string Directory => _dir;

    public AcpSession Create(string? model, AppConfig cfg)
    {
        var now = DateTime.UtcNow;
        var state = new SessionState
        {
            Id = NewSessionId(),
            Model = model ?? cfg.Llm.Model,
        };
        state.Meta.TokenTracker ??= new TokenTracker();
        // Default to plan mode (read-only). The extension UI also defaults to "plan", but it
        // only transmits the mode on an explicit toggle — so without this default a fresh
        // session would silently run in build mode (writes allowed) while the UI showed "plan".
        state.Meta.PlanMode = true;

        var session = new AcpSession { State = state, LastActivityAt = now };
        _live[session.Id] = session;
        Save(session);
        return session;
    }

    public AcpSession? TryGet(string id)
    {
        if (!IsValidId(id)) return null;

        if (_live.TryGetValue(id, out var live))
            return IsExpired(live) ? null : live;

        SessionState? state;
        lock (_ioLock)
            state = _sessions.LoadAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        if (state is null) return null;

        SessionConsistency.Repair(state);

        var session = new AcpSession
        {
            State = state,
            LastActivityAt = state.Messages.Count > 0 ? state.Messages[^1].Timestamp : state.StartedAt,
        };
        if (IsExpired(session)) return null;

        _live[id] = session;
        return session;
    }

    public IReadOnlyList<SessionSummary> List(int limit = 200)
    {
        lock (_ioLock)
            return _sessions.ListSessionsAsync(limit, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Save(AcpSession session)
    {
        _live[session.Id] = session;
        lock (_ioLock)
            _sessions.SaveAsync(session.State, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Delete(string id)
    {
        _live.TryRemove(id, out _);
        if (!IsValidId(id)) return;
        lock (_ioLock)
            _sessions.DeleteAsync(id, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void PurgeExpired(TimeSpan ttl)
    {
        if (ttl == Timeout.InfiniteTimeSpan) return;
        var cutoff = DateTime.UtcNow - ttl;

        foreach (var (id, session) in _live)
            if (session.LastActivityAt < cutoff)
                Delete(id);

        IReadOnlyList<SessionSummary> summaries;
        lock (_ioLock)
            summaries = _sessions.ListSessionsAsync(int.MaxValue, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var s in summaries)
            if (s.LastActivityAt < cutoff)
                Delete(s.Id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reaper?.Dispose();
    }

    private bool IsExpired(AcpSession session)
    {
        if (_ttl == Timeout.InfiniteTimeSpan) return false;
        if (DateTime.UtcNow - session.LastActivityAt <= _ttl) return false;
        Delete(session.Id);
        return true;
    }

    private void MigrateLegacyBlobs(AppConfig cfg, AcpServerSettings settings)
    {
        var legacyDir = settings.SessionsDirectory;
        if (string.IsNullOrWhiteSpace(legacyDir) || !System.IO.Directory.Exists(legacyDir))
            return;

        var migratedDir = Path.Combine(legacyDir, "migrated");

        foreach (var file in System.IO.Directory.EnumerateFiles(legacyDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var blob = JsonSerializer.Deserialize<LegacyBlob>(json, LegacyJsonOpts);
                if (blob is null || !IsValidId(blob.Id))
                {
                    Quarantine(file);
                    continue;
                }

                if (System.IO.Directory.GetFiles(_dir, $"*_{blob.Id}.jsonl").Length == 0)
                {
                    var state = new SessionState
                    {
                        Id = blob.Id,
                        StartedAt = blob.StartedAt == default ? DateTime.UtcNow : blob.StartedAt,
                        Model = blob.Model,
                    };
                    state.TurnCount = blob.TurnCount;
                    state.Meta.PlanMode = blob.PlanMode;
                    if (blob.Todos is { Count: > 0 }) state.Todos.AddRange(blob.Todos);
                    if (blob.Messages is { Count: > 0 })
                        foreach (var m in blob.Messages) state.AddMessage(m);

                    lock (_ioLock)
                        _sessions.SaveAsync(state, CancellationToken.None).GetAwaiter().GetResult();
                }

                System.IO.Directory.CreateDirectory(migratedDir);
                File.Move(file, Path.Combine(migratedDir, Path.GetFileName(file)), overwrite: true);
            }
            catch (JsonException)
            {
                Quarantine(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            var corruptPath = path + ".corrupt";
            if (File.Exists(corruptPath)) File.Delete(corruptPath);
            File.Move(path, corruptPath);
        }
        catch
        {
        }
    }

    private static readonly JsonSerializerOptions LegacyJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record LegacyBlob
    {
        public string Id { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public string? Model { get; init; }
        public int TurnCount { get; init; }
        public bool PlanMode { get; init; }
        public List<TodoItem>? Todos { get; init; }
        public List<Message>? Messages { get; init; }
    }

    private static string NewSessionId() => "sess_" + Guid.NewGuid().ToString("N")[..16];

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("sess_", StringComparison.Ordinal)) return false;
        for (var i = 5; i < id.Length; i++)
        {
            var c = id[i];
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        }
        return id.Length > 5;
    }
}
