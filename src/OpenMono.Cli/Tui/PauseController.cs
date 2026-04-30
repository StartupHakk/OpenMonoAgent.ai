namespace OpenMono.Tui;

public sealed class PauseController
{
    private volatile bool _isPaused;
    private TaskCompletionSource? _pauseTcs;
    private readonly object _lock = new();

    public bool IsPaused => _isPaused;

    public event EventHandler<bool>? OnPauseStateChanged;

    public void TogglePause()
    {
        lock (_lock)
        {
            if (_isPaused)
                Resume();
            else
                Pause();
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        TaskCompletionSource? tcs;
        lock (_lock)
        {
            if (!_isPaused)
                return;
            tcs = _pauseTcs;
        }

        if (tcs is null)
            return;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    private void Pause()
    {
        _isPaused = true;
        _pauseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OnPauseStateChanged?.Invoke(this, true);
    }

    private void Resume()
    {
        var tcs = _pauseTcs;
        _isPaused = false;
        _pauseTcs = null;
        tcs?.TrySetResult();
        OnPauseStateChanged?.Invoke(this, false);
    }
}
