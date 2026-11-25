namespace OpenMono.Session;

/// <summary>Kind of session-state mutation, carried on <see cref="SessionSettings.StateChanged"/>.</summary>
public enum StateChangeKind
{
    Thinking,
    Mode,
    Host,
    AutoApprove,
    Git,
    Backend,
    Model,
    Todos,
}

/// <summary>
/// Event raised after a <see cref="SessionSettings"/> mutator changes session
/// state. Frontends (TUI renderer, ACP SSE stream) use it to refresh UI; the
/// persist callback is invoked before the event fires, so listeners always see
/// a state that is already on disk.
/// </summary>
public sealed record StateChangedEventArgs(StateChangeKind Kind, object? Payload = null);

/// <summary>
/// Single owner of every mutable session-setting mutation. Both frontends (TUI
/// slash-commands, ACP HTTP endpoints) route writes through here so validation
/// (capability clamping), revision bumping, write-through persistence and
/// change broadcasting happen exactly once, in one place.
/// </summary>
/// <remarks>
/// Construction is cheap — one per session per process, wired with the
/// process's persist callback (<c>session =&gt; store.Save(session)</c> for ACP,
/// <c>SaveAsync</c> for the TUI). Mutators never throw for invalid input; they
/// clamp or no-op and report through the returned result so callers can render
/// a useful message.
/// </remarks>
public sealed class SessionSettings
{
    private readonly SessionState _session;
    private readonly string _writer;
    private readonly Func<SessionState, Task>? _persist;
    private readonly Func<Utils.ModelReasoningProfile>? _reasoningProfile;

    public event Action<StateChangedEventArgs>? StateChanged;

    /// <param name="session">The live session whose settings this instance owns.</param>
    /// <param name="writer">"tui" or "acp" — stamped into the header for diagnostics.</param>
    /// <param name="persist">Write-through callback invoked (awaited) after every mutation.</param>
    /// <param name="reasoningProfile">
    /// Resolves the reasoning profile of the session's CURRENT model (server
    /// advertised first, name heuristics as fallback). Mutators clamp thinking
    /// against its levels, so a model switch must keep this in sync.
    /// </param>
    public SessionSettings(
        SessionState session,
        string writer,
        Func<SessionState, Task>? persist = null,
        Func<Utils.ModelReasoningProfile>? reasoningProfile = null)
    {
        _session = session;
        _writer = writer;
        _persist = persist;
        _reasoningProfile = reasoningProfile;
    }

    public SessionState Session => _session;

    /// <summary>The reasoning profile of the current model (heuristic when no server info).</summary>
    public Utils.ModelReasoningProfile ReasoningProfile
        => _reasoningProfile?.Invoke() ?? Utils.ModelReasoningProfile.Resolve(_session.Model);

    // ---------------------------------------------------------------- thinking

    /// <summary>
    /// Set thinking to a specific level ("off" disables). The level is clamped
    /// to what the current model advertises; an unknown level returns
    /// <c>applied=false</c> with the valid list so the caller can show it.
    /// </summary>
    public async Task<ThinkingResult> SetThinkingAsync(string level, CancellationToken ct = default)
    {
        var profile = ReasoningProfile;

        if (profile.Kind == Utils.ReasoningKind.BinaryToggle)
        {
            var on = level is not ("off" or "0" or "false");
            return await ApplyThinkingAsync(on ? "on" : "off", ct);
        }

        if (profile.Kind == Utils.ReasoningKind.None)
            return new ThinkingResult(false, null, "This model has no thinking support.", profile.Levels);

        var idx = Utils.ThinkingLevels.Resolve(level, profile.Levels);
        if (idx < 0)
        {
            // Clamp: keep the session's level only if the model still offers it,
            // otherwise fall back to the profile default.
            var current = _session.Meta.ThinkingLevel;
            if (current is not null && Array.IndexOf(profile.Levels, current) >= 0)
                return new ThinkingResult(false, current, $"Unknown level '{level}'.", profile.Levels);
            return await ApplyThinkingAsync(profile.DefaultLevel, ct);
        }

        return await ApplyThinkingAsync(profile.Levels[idx], ct);
    }

    /// <summary>Cycle to the next thinking level (or toggle on binary models).</summary>
    public async Task<ThinkingResult> CycleThinkingAsync(string? requestedLevel = null, CancellationToken ct = default)
    {
        var profile = ReasoningProfile;

        if (profile.Kind == Utils.ReasoningKind.None)
            return new ThinkingResult(false, null, "This model has no thinking support.", profile.Levels);

        if (profile.Kind == Utils.ReasoningKind.BinaryToggle)
            return await ApplyThinkingAsync(_session.Meta.ThinkingEnabled ? "off" : "on", ct);

        var levels = profile.Levels;
        var current = _session.Meta.ThinkingLevel ?? "off";
        var idx = Array.IndexOf(levels, current);
        if (idx < 0) idx = 0;

        var nextIdx = requestedLevel is not null
            ? Utils.ThinkingLevels.Resolve(requestedLevel, levels)
            : (idx + 1) % levels.Length;

        if (nextIdx < 0)
            return new ThinkingResult(false, current, $"Unknown level '{requestedLevel}'. Use: {string.Join(", ", levels)}", levels);

        return await ApplyThinkingAsync(levels[nextIdx], ct);
    }

    private async Task<ThinkingResult> ApplyThinkingAsync(string level, CancellationToken ct)
    {
        var changed = _session.Meta.ThinkingLevel != level ||
                      _session.Meta.ThinkingEnabled != (level != "off");
        if (changed)
        {
            _session.Meta.ThinkingLevel = level;
            _session.Meta.ThinkingEnabled = level != "off";
            Commit();
            await PersistAsync(ct);
            Raise(StateChangeKind.Thinking, new { level, enabled = level != "off" });
        }
        return new ThinkingResult(true, level, null, ReasoningProfile.Levels);
    }

    /// <summary>
    /// Clamp the persisted thinking level to what the CURRENT profile advertises
    /// (called on load/resume and after every model switch). Returns true when
    /// the level had to change.
    /// </summary>
    public async Task<bool> ClampThinkingToCapabilitiesAsync(CancellationToken ct = default)
    {
        var profile = ReasoningProfile;
        var level = _session.Meta.ThinkingLevel;

        string? fixedLevel = profile.Kind switch
        {
            Utils.ReasoningKind.None => "off",
            Utils.ReasoningKind.BinaryToggle => level is "on" or "off" or null ? level : (_session.Meta.ThinkingEnabled ? "on" : "off"),
            _ => level is not null && Array.IndexOf(profile.Levels, level) >= 0
                ? level
                : profile.DefaultLevel,
        };

        if (fixedLevel == level && _session.Meta.ThinkingEnabled == (fixedLevel != "off"))
            return false;

        await ApplyThinkingAsync(fixedLevel ?? profile.DefaultLevel, ct);
        return true;
    }

    // -------------------------------------------------------------- the rest

    public async Task SetPlanModeAsync(bool enabled, CancellationToken ct = default)
    {
        if (_session.Meta.PlanMode == enabled) return;
        _session.Meta.PlanMode = enabled;
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.Mode, new { mode = enabled ? "plan" : "build" });
    }

    public async Task SetHostEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (_session.Meta.HostEnabled == enabled) return;
        _session.Meta.HostEnabled = enabled;
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.Host, new { host_enabled = enabled });
    }

    public async Task SetAutoApproveAsync(bool enabled, CancellationToken ct = default)
    {
        if (_session.Meta.AutoApproveWrites == enabled) return;
        _session.Meta.AutoApproveWrites = enabled;
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.AutoApprove, new { auto_approve = enabled });
    }

    public async Task SetGitProfileAsync(string? profileName, bool touched, CancellationToken ct = default)
    {
        if (_session.Meta.GitProfileName == profileName && _session.Meta.GitProfileTouched == touched) return;
        _session.Meta.GitProfileName = profileName;
        _session.Meta.GitProfileTouched = touched;
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.Git, new { profile = profileName });
    }

    public async Task SetBackendAsync(string? backendName, CancellationToken ct = default)
    {
        if (_session.ActiveBackend == backendName) return;
        _session.ActiveBackend = backendName;
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.Backend, new { backend = backendName });
    }

    /// <summary>
    /// Switch the session model. Does NOT re-probe capabilities (that's the
    /// caller's job via <see cref="Utils.ReasoningCapabilityCache"/>); after the
    /// new server info is visible through <c>reasoningProfile</c>, call
    /// <see cref="ClampThinkingToCapabilitiesAsync"/> so thinking stays valid.
    /// </summary>
    public async Task SetModelAsync(string model, CancellationToken ct = default)
    {
        if (string.Equals(_session.Model, model, StringComparison.OrdinalIgnoreCase)) return;
        _session.Model = model;
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.Model, new { model });
    }

    public async Task SetTodosAsync(IEnumerable<TodoItem> todos, CancellationToken ct = default)
    {
        _session.Todos.Clear();
        _session.Todos.AddRange(todos);
        Commit();
        await PersistAsync(ct);
        Raise(StateChangeKind.Todos, new { count = _session.Todos.Count });
    }

    /// <summary>
    /// Record a mutation the pipeline couldn't intercept (e.g. done directly on
    /// Meta by older code paths) so the header revision still advances.
    /// </summary>
    public void Commit() => _session.Meta.BumpRevision(_writer);

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_persist is null) return;
        try
        {
            await _persist(_session);
        }
        catch (Exception ex)
        {
            Utils.Log.Warn($"SessionSettings persist failed: {ex.Message}");
        }
    }

    private void Raise(StateChangeKind kind, object? payload)
        => StateChanged?.Invoke(new StateChangedEventArgs(kind, payload));

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Attach a settings pipeline to <paramref name="session"/>, wiring the
    /// persist callback and event stream through <paramref name="hooks"/>.
    /// </summary>
    public static SessionSettings Attach(
        SessionState session,
        string writer,
        Func<SessionState, Task>? persist = null,
        Func<Utils.ModelReasoningProfile>? reasoningProfile = null,
        Action<StateChangedEventArgs>? onChanged = null)
    {
        var settings = new SessionSettings(session, writer, persist, reasoningProfile);
        if (onChanged is not null)
            settings.StateChanged += onChanged;
        return settings;
    }
}

/// <summary>Outcome of a thinking mutation from <see cref="SessionSettings"/>.</summary>
public sealed record ThinkingResult(bool Applied, string? Level, string? Error, string[] Levels)
{
    public string Describe() => Applied
        ? (Level == "off" ? "Thinking: OFF" : $"Thinking: {Utils.ThinkingLevels.Describe(Level!)}")
        : Error ?? "No change";
}
