using System.Diagnostics;

namespace OpenMono.Tui;

public sealed class StreamingMetrics
{
    private readonly Stopwatch _stopwatch = new();
    private int _totalCompletionTokens;
    private bool _isStreaming;

    private readonly Queue<(long ticksElapsed, int tokens)> _samples = new();
    private static readonly long RollingWindowTicks = TimeSpan.FromSeconds(2).Ticks;

    public bool IsStreaming => _isStreaming;
    public double TokensPerSecond { get; private set; }
    public int TotalCompletionTokens => _totalCompletionTokens;

    public void OnStreamStart()
    {
        _stopwatch.Restart();
        _totalCompletionTokens = 0;
        _samples.Clear();
        TokensPerSecond = 0;
        _isStreaming = true;
    }

    public void OnTokenReceived(int totalCompletionTokens)
    {
        var elapsed = _stopwatch.ElapsedTicks;
        _totalCompletionTokens = totalCompletionTokens;
        _samples.Enqueue((elapsed, totalCompletionTokens));

        var cutoff = elapsed - RollingWindowTicks;
        while (_samples.Count > 1 && _samples.Peek().ticksElapsed < cutoff)
            _samples.Dequeue();

        if (_samples.Count >= 2)
        {
            var oldest = _samples.Peek();
            var tokenDelta = totalCompletionTokens - oldest.tokens;
            var timeDelta = (elapsed - oldest.ticksElapsed) / (double)Stopwatch.Frequency;
            TokensPerSecond = timeDelta > 0 ? tokenDelta / timeDelta : 0;
        }
        else if (_stopwatch.Elapsed.TotalSeconds > 0)
        {
            TokensPerSecond = totalCompletionTokens / _stopwatch.Elapsed.TotalSeconds;
        }
    }

    public void OnStreamEnd()
    {
        _stopwatch.Stop();
        _isStreaming = false;
    }
}
