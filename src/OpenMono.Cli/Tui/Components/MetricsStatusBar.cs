using OpenMono.Tui.Keybindings;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public class MetricsStatusBar : View
{
    private readonly Label _tokSecLabel;
    private readonly Label _totalLabel;
    private readonly Label _contextLabel;
    private readonly Label _hintsLabel;
    private readonly StreamingMetrics _metrics;
    private readonly ContextWindowMeter _contextMeter;
    private readonly KeybindingManager _keybindings;
    private readonly object? _timerToken;

    private const string Separator = " \u2502 ";
    private bool _isStreaming;
    private bool _isPaused;
    private bool _approvalMode;

    public MetricsStatusBar(IApplication app, KeybindingManager keybindings, int contextSize = 128_000)
    {
        Height = 1;
        Width = Dim.Fill();
        CanFocus = false;

        _keybindings = keybindings;
        _metrics = new StreamingMetrics();
        _contextMeter = new ContextWindowMeter(contextSize);

        _tokSecLabel = new Label
        {
            Text = "\u25c9 0.0 tok/s",
            X = 0,
            Y = 0,
            Width = Dim.Auto()
        };

        var sep1 = new Label { Text = Separator, X = Pos.Right(_tokSecLabel), Y = 0, Width = Dim.Auto() };

        _totalLabel = new Label
        {
            Text = "0 total",
            X = Pos.Right(sep1),
            Y = 0,
            Width = Dim.Auto()
        };

        var sep2 = new Label { Text = Separator, X = Pos.Right(_totalLabel), Y = 0, Width = Dim.Auto() };

        _contextLabel = new Label
        {
            Text = FormatContextText(),
            X = Pos.Right(sep2),
            Y = 0,
            Width = Dim.Auto()
        };

        var sep3 = new Label { Text = Separator, X = Pos.Right(_contextLabel), Y = 0, Width = Dim.Auto() };

        _hintsLabel = new Label
        {
            Text = BuildIdleHints(),
            X = Pos.Right(sep3),
            Y = 0,
            Width = Dim.Auto()
        };

        Add(_tokSecLabel, sep1, _totalLabel, sep2, _contextLabel, sep3, _hintsLabel);

        _timerToken = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (_isStreaming)
                RefreshLabels();
            return true;
        });
    }

    public StreamingMetrics Metrics => _metrics;
    public ContextWindowMeter ContextMeter => _contextMeter;
    public bool IsPaused => _isPaused;

    public void OnStreamStart()
    {
        _isStreaming = true;
        _metrics.OnStreamStart();
        _hintsLabel.Text = BuildStreamingHints();
    }

    public void OnTokenReceived(int totalCompletionTokens)
    {
        _metrics.OnTokenReceived(totalCompletionTokens);
    }

    public void OnStreamEnd()
    {
        _isStreaming = false;
        _metrics.OnStreamEnd();
        _hintsLabel.Text = BuildIdleHints();
        RefreshLabels();
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        UpdateHints();
    }

    public void SetApprovalMode(bool approvalMode)
    {
        _approvalMode = approvalMode;
        UpdateHints();
    }

    private void UpdateHints()
    {
        _hintsLabel.Text = _isStreaming ? BuildStreamingHints() : BuildIdleHints();
        SetNeedsDraw();
    }

    public void UpdateContext(int promptTokens)
    {
        _contextMeter.Update(promptTokens);
        _contextLabel.Text = FormatContextText();
    }

    private void RefreshLabels()
    {
        _tokSecLabel.Text = $"\u25c9 {_metrics.TokensPerSecond:F1} tok/s";
        _totalLabel.Text = $"{_metrics.TotalCompletionTokens:N0} total";
        _contextLabel.Text = FormatContextText();
    }

    private string FormatContextText()
    {
        var bar = _contextMeter.FormatProgressBar(10);
        var pct = _contextMeter.UsagePercent;
        return $"{bar} {pct:F0}% ctx";
    }

    private string BuildStreamingHints()
    {
        var pause = _keybindings.GetHint(TuiAction.Pause);
        var verb = _isPaused ? "Resume" : "Pause";
        return $"{pause} {verb} \u2502 ^C Cancel";
    }

    private string BuildIdleHints()
    {
        var approval = _keybindings.GetHint(TuiAction.ToggleApproval);
        var sidebar = _keybindings.GetHint(TuiAction.ToggleSidebar);
        var help = _keybindings.GetHint(TuiAction.Help);
        var approvalLabel = _approvalMode ? "APPROVAL \u2502 " : "";
        return $"{approvalLabel}{approval} {(_approvalMode ? "Auto" : "Approval")} \u2502 {sidebar} Sidebar \u2502 {help} Help";
    }
}
