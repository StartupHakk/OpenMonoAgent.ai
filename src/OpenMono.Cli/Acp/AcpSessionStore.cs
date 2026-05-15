using System.Collections.Concurrent;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Acp;

public sealed class AcpSessionStore : IDisposable
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new();
    private readonly TimeSpan _ttl;
    private readonly Timer? _reaper;
    private readonly object _ioLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
    private bool _disposed;

    public AcpSessionStore(AppConfig cfg, AcpServerSettings settings, bool startReaper = true)
    {
        _dir = Path.Combine(cfg.DataDirectory, "acp-sessions");
        Directory.CreateDirectory(_dir);
        _ttl = TimeSpan.FromHours(settings.SessionTtlHours);
        Hydrate();
        if (startReaper)
        {
            var period = TimeSpan.FromMinutes(5);
            _reaper = new Timer(_ => PurgeExpired(_ttl), null, period, period);
        }
    }

    public AcpSession Create(string? model, IReadOnlyList<string>? clientTools, AppConfig cfg)
    {
        var now = DateTime.UtcNow;
        var session = new AcpSession
        {
            Id = NewSessionId(),
            StartedAt = now,
            Model = model ?? cfg.Llm.Model,
            ClientTools = clientTools is null ? Array.Empty<string>() : clientTools.ToArray(),
            LastActivityAt = now,
        };
        _sessions[session.Id] = session;
        Save(session);
        return session;
    }

    public AcpSession? TryGet(string id)
    {
        if (!IsValidId(id)) return null;
        if (!_sessions.TryGetValue(id, out var session)) return null;
        if (DateTime.UtcNow - session.LastActivityAt > _ttl)
        {
            Delete(id);
            return null;
        }
        return session;
    }

    public void Save(AcpSession session)
    {
        _sessions[session.Id] = session;
        var path = PathFor(session.Id);
        var tmp = path + ".tmp";
        lock (_ioLock)
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public void Delete(string id)
    {
        _sessions.TryRemove(id, out _);
        if (!IsValidId(id)) return;
        var path = PathFor(id);
        lock (_ioLock)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public void PurgeExpired(TimeSpan ttl)
    {
        var cutoff = DateTime.UtcNow - ttl;
        foreach (var (id, session) in _sessions)
        {
            if (session.LastActivityAt < cutoff)
                Delete(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reaper?.Dispose();
    }

    private void Hydrate()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<AcpSession>(json, JsonOpts);
                if (session is not null) _sessions[session.Id] = session;
            }
            catch
            {
                // Corrupt session file; skip. Cannot log here without DI; rely on disk
                // forensics. T9 may add a structured warning.
            }
        }
    }

    private string PathFor(string id) => Path.Combine(_dir, $"{id}.json");

    private static string NewSessionId() => "sess_" + Guid.NewGuid().ToString("N")[..16];

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("sess_", StringComparison.Ordinal)) return false;
        for (int i = 5; i < id.Length; i++)
        {
            var c = id[i];
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        }
        return id.Length > 5;
    }
}
