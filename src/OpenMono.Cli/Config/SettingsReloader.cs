namespace OpenMono.Config;

/// <summary>
/// Watches <c>~/.openmono/settings.json</c> and, after a short debounce, runs the
/// caller-supplied <paramref name="reload"/> closure. Lets a running session pick up
/// <c>openmono config set llm.*</c> (or any manual edit of the file) without a restart.
///
/// <para>
/// The watcher is best-effort: an unwatchable filesystem (a missing directory that is
/// created later, a network mount, etc.) is tolerated — the file is still applied on the
/// next start. Debouncing (a 150 ms reset timer) collapses the burst of
/// <see cref="FileSystemWatcher"/> events a single <c>config set</c> write produces.
/// </para>
/// </summary>
public sealed class SettingsReloader : IDisposable
{
    private readonly string _settingsPath;
    private readonly Func<Task> _reload;
    private readonly Action<string> _warn;
    private FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private readonly object _lock = new();
    private bool _scheduled;
    private bool _disposed;

    /// <param name="settingsPath">The settings.json to watch (e.g. <c>~/.openmono/settings.json</c>).</param>
    /// <param name="reload">Runs (debounced) when the file changes. Must never throw.</param>
    /// <param name="warn">Sink for watcher-lifecycle warnings (file missing at start, etc.).</param>
    public SettingsReloader(string settingsPath, Func<Task> reload, Action<string> warn)
    {
        _settingsPath = settingsPath;
        _reload = reload;
        _warn = warn;
        _debounce = new Timer(_ => DebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        StartWatcher();
    }

    private void StartWatcher()
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        var name = Path.GetFileName(_settingsPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            // The data directory does not exist yet (fresh install). Nothing to watch;
            // the file will be created on first 'config set' and picked up next start.
            _warn($"settings: could not watch {_settingsPath} (directory missing) — config changes apply on next start.");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _warn($"settings: watcher unavailable ({ex.Message}) — config changes apply on next start.");
            _watcher = null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule();

    private void Schedule()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _scheduled = true;
        }
        _debounce.Change(150, Timeout.Infinite);
    }

    private async void DebounceElapsed()
    {
        bool fire;
        lock (_lock)
        {
            fire = _scheduled;
            _scheduled = false;
        }
        if (!fire) return;

        try
        {
            await _reload().ConfigureAwait(false);
        }
        catch
        {
            // Reload failures must never take down the process. The closure is
            // responsible for its own diagnostics.
        }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _debounce.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
