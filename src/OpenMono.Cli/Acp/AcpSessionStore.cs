using System.Collections.Concurrent;
using System.Text.Json;
using OpenMono.Cli.Git;
using OpenMono.Config;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Acp;

public sealed class AcpSessionStore : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly string _dir;
    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, AcpSession> _live = new();
    private readonly TimeSpan _ttl;
    private readonly Timer? _reaper;
    private readonly object _ioLock = new();
    private bool _disposed;

    public AcpSessionStore(AppConfig cfg, AcpServerSettings settings, bool startReaper = true)
    {
        _config = cfg;
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

    internal ModelReasoningProfile ResolveReasoning(string? modelName)
        => ModelReasoningProfile.Resolve(modelName, ResolvedServerReasoning(modelName));

    /// <summary>Server-advertised reasoning for a model: capability cache first
    /// (shared across processes + refreshed on /model switches), falling back to
    /// the startup probe result on config.Llm.</summary>
    internal ServerReasoningInfo? ResolvedServerReasoning(string? modelName)
        => _config.Capabilities.Get(_config.Llm.Endpoint, modelName) ?? _config.Llm.ServerReasoning;

    public AcpSession Create(string? model, AppConfig cfg, string? backendName = null)
    {
        var now = DateTime.UtcNow;
        var state = new SessionState
        {
            Id = NewSessionId(),
            Model = model ?? cfg.Llm.Model,
            ActiveBackend = backendName,
        };
        state.Meta.TokenTracker ??= new TokenTracker();
        // Default to plan mode (read-only). The extension UI also defaults to "plan", but it
        // only transmits the mode on an explicit toggle — so without this default a fresh
        // session would silently run in build mode (writes allowed) while the UI showed "plan".
        state.Meta.PlanMode = true;

        // Inherit the git auth profile resolved at startup
        // (OPENMONO_GIT_PROFILE / git.default) so every ACP session authenticates
        // as the same profile. The POST /sessions/{id}/git endpoint hot-swaps
        // this per session afterwards; a resumed session keeps its own persisted
        // choice (AcpSession.TryGet → LoadAsync restores GitProfileName).
        var (gitProfileName, _) = GitProfileSelection.Resolve(cfg.Git, null);
        if (gitProfileName is not null)
        {
            state.Meta.GitProfileName = gitProfileName;
            state.Meta.GitProfileTouched = cfg.Git.SessionProfile is not null;
        }

        var reasoningProfile = ResolveReasoning(state.Model);
        state.Meta.ThinkingLevel = reasoningProfile.DefaultLevel;
        state.Meta.ThinkingEnabled = reasoningProfile.DefaultEnabled && reasoningProfile.DefaultLevel != "off";

        var session = new AcpSession { State = state, LastActivityAt = now };
        AttachSettings(session);
        _live[session.Id] = session;
        Save(session);
        return session;
    }

    public AcpSession? TryGet(string id)
    {
        if (!IsValidId(id)) return null;

        if (_live.TryGetValue(id, out var live))
        {
            if (IsExpired(live)) return null;
            RefreshIfDiskNewer(live);
            return live;
        }

        SessionState? state;
        lock (_ioLock)
            state = _sessions.LoadAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        if (state is null) return null;

        SessionConsistency.Repair(state);

        if (state.Meta.ThinkingLevel is null)
        {
            var legacyProfile = ResolveReasoning(state.Model);
            state.Meta.ThinkingLevel = legacyProfile.DefaultLevel;
            state.Meta.ThinkingEnabled = legacyProfile.DefaultEnabled && legacyProfile.DefaultLevel != "off";
        }

        var session = new AcpSession
        {
            State = state,
            LastActivityAt = state.Messages.Count > 0 ? state.Messages[^1].Timestamp : state.StartedAt,
            DiskStamp = DiskStampFor(state.Id),
        };
        if (IsExpired(session)) return null;

        AttachSettings(session);
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
        session.DiskStamp = DiskStampFor(session.Id);
    }

    public void Delete(string id)
    {
        _live.TryRemove(id, out _);
        if (!IsValidId(id)) return;
        lock (_ioLock)
            _sessions.DeleteAsync(id, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Get (or create) the per-session diff stager for the given session id, backed by
    /// the shared data directory. Used by the diffs/undo/redo endpoints.
    /// </summary>
    public OpenMono.Session.SessionDiffStager GetDiffStager(string id)
        => new OpenMono.Session.SessionDiffStager(_config.DataDirectory, id);

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

    /// <summary>
    /// Cross-process freshness: if the session file on disk was written after our
    /// last write (e.g. a TUI resumed the same session and saved), reload it and
    /// merge into the live in-memory copy before handing the session out.
    /// Messages union by content so interleaved writes from both processes are
    /// preserved; settings resolve by revision via <see cref="SessionMetaMerger"/>.
    /// </summary>
    private void RefreshIfDiskNewer(AcpSession live)
    {
        var current = DiskStampFor(live.Id);
        if (current is null) return;
        if (live.DiskStamp is null) { live.DiskStamp = current; return; }
        if (current <= live.DiskStamp) return;

        SessionState? loaded;
        lock (_ioLock)
            loaded = _sessions.LoadAsync(live.Id, CancellationToken.None).GetAwaiter().GetResult();
        if (loaded is null) return;

        live.DiskStamp = current;

        var changed = SessionMetaMerger.Merge(live.State, loaded);
        changed |= UnionMessages(live.State, loaded);
        if (loaded.Checkpoints.Count > live.State.Checkpoints.Count)
        {
            live.State.Checkpoints.Clear();
            live.State.Checkpoints.AddRange(loaded.Checkpoints);
            live.State.CheckpointCutoffIndex = loaded.CheckpointCutoffIndex;
            changed = true;
        }

        if (changed)
        {
            Log.Info($"[ACP] session {live.Id}: merged newer on-disk state (rev {loaded.Meta.Revision} vs live)");
            live.Settings?.Commit();
            Save(live);
        }
    }

    /// <summary>
    /// Append messages the live copy is missing. Exact-duplicate detection uses
    /// role+timestamp+content so messages each process wrote independently are
    /// not duplicated when both copies are merged.
    /// </summary>
    private static bool UnionMessages(SessionState live, SessionState loaded)
    {
        static string Key(Message m) =>
            $"{m.Role}|{m.Timestamp.Ticks}|{m.Content}|{m.ToolCallId}";

        var liveKeys = new HashSet<string>(live.Messages.Select(Key));
        var added = 0;
        foreach (var msg in loaded.Messages)
        {
            if (liveKeys.Add(Key(msg)))
            {
                live.Messages.Add(msg);
                added++;
            }
        }
        if (added > 0)
            live.Messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return added > 0;
    }

    /// <summary>mtime of the session file, or null when it doesn't exist.</summary>
    private DateTime? DiskStampFor(string sessionId)
    {
        try
        {
            var files = System.IO.Directory.GetFiles(_dir, $"*_{sessionId}.jsonl");
            if (files.Length == 0) return null;
            return File.GetLastWriteTimeUtc(files[0]);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wire the single settings pipeline for a session: every mutation clamps to
    /// server-advertised capabilities, bumps the revision, write-throughs to disk
    /// (via this store, so DiskStamp stays accurate), and broadcasts a
    /// state_changed event to the session's current SSE sink.
    /// </summary>
    private void AttachSettings(AcpSession session)
    {
        if (session.Settings is not null) return;
        session.Settings = SessionSettings.Attach(
            session.State,
            "acp",
            persist: state => Task.Run(() => Save(session)),
            reasoningProfile: () => ResolveReasoning(session.State.Model),
            onChanged: args => session.CurrentEventSink?.Invoke("state_changed", args));
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
        if (string.IsNullOrEmpty(id)) return false;
        if (id.StartsWith("sess_", StringComparison.Ordinal))
        {
            if (id.Length <= 5) return false;
            for (var i = 5; i < id.Length; i++)
            {
                var c = id[i];
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
            }
            return true;
        }

        // Legacy TUI sessions used a bare 12-hex id (pre-sess_ standardization);
        // accept them so the extension can join sessions created by the TUI.
        if (id.Length is >= 8 and <= 32)
        {
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
            }
            return true;
        }
        return false;
    }
}
