namespace OpenMono.Tui;

public sealed class ContextWindowMeter
{
    private readonly int _contextSize;
    private int _promptTokens;

    public ContextWindowMeter(int contextSize = 128_000)
    {
        _contextSize = contextSize > 0 ? contextSize : 128_000;
    }

    public int PromptTokens => _promptTokens;
    public double UsagePercent => _contextSize > 0 ? (double)_promptTokens / _contextSize * 100 : 0;
    public int RemainingTokens => Math.Max(0, _contextSize - _promptTokens);

    public void Update(int promptTokens)
    {
        _promptTokens = promptTokens;
    }

    public string FormatRemaining()
    {
        var remaining = RemainingTokens;
        return remaining >= 1000
            ? $"{remaining / 1000.0:F1}K remaining"
            : $"{remaining} remaining";
    }

    public string FormatProgressBar(int width = 10)
    {
        var percent = Math.Clamp(UsagePercent / 100.0, 0, 1);
        var filled = (int)Math.Round(percent * width);
        var empty = width - filled;
        return new string('\u2588', filled) + new string('\u2591', empty);
    }
}
