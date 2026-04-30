using System.Diagnostics;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public sealed class ToolEntry
{
    public required string ToolId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgsSummary { get; init; }
    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    public float Progress { get; set; }
    public bool Completed { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
}

public class ToolMonitorPanel : FrameView
{
    private readonly View _activeSection;
    private readonly View _historySection;
    private readonly Label _historySeparator;
    private readonly Label _historyHeader;

    private readonly List<ToolEntry> _activeTools = [];
    private readonly List<ToolEntry> _history = [];
    private const int MaxHistory = 5;

    public ToolMonitorPanel()
    {
        Title = "Tools";
        Width = 40;
        BorderStyle = LineStyle.Single;
        CanFocus = false;

        _activeSection = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Auto(DimAutoStyle.Content)
        };

        _historySeparator = new Label
        {
            Text = new string('\u2500', 36),
            X = 0,
            Y = Pos.Bottom(_activeSection),
            Width = Dim.Fill()
        };

        _historyHeader = new Label
        {
            Text = "History (last 5):",
            X = 0,
            Y = Pos.Bottom(_historySeparator),
            Width = Dim.Fill()
        };

        _historySection = new View
        {
            X = 0,
            Y = Pos.Bottom(_historyHeader),
            Width = Dim.Fill(),
            Height = Dim.Auto(DimAutoStyle.Content)
        };

        Add(_activeSection, _historySeparator, _historyHeader, _historySection);
    }

    public void ToolStarted(string toolId, string toolName, string argsSummary)
    {
        var entry = new ToolEntry
        {
            ToolId = toolId,
            ToolName = toolName,
            ArgsSummary = argsSummary.Length > 30 ? argsSummary[..30] + "..." : argsSummary
        };
        _activeTools.Add(entry);
        RebuildActiveSection();
    }

    public void ToolProgress(string toolId, float progress)
    {
        var entry = _activeTools.Find(t => t.ToolId == toolId);
        if (entry is not null)
        {
            entry.Progress = Math.Clamp(progress, 0f, 1f);
            RebuildActiveSection();
        }
    }

    public void ToolCompleted(string toolId, bool success, string? error, TimeSpan? duration = null)
    {
        var entry = _activeTools.Find(t => t.ToolId == toolId);
        if (entry is null)
        {

            entry = new ToolEntry
            {
                ToolId = toolId,
                ToolName = "unknown",
                ArgsSummary = ""
            };
        }
        else
        {
            _activeTools.Remove(entry);
        }

        entry.Completed = true;
        entry.Success = success;
        entry.Error = error;
        entry.Duration = duration ?? entry.Elapsed.Elapsed;
        entry.Elapsed.Stop();

        _history.Insert(0, entry);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(_history.Count - 1);

        RebuildActiveSection();
        RebuildHistorySection();
    }

    private void RebuildActiveSection()
    {
        _activeSection.RemoveAll();

        var y = 0;
        foreach (var tool in _activeTools)
        {
            var elapsed = tool.Elapsed.Elapsed;
            var bar = FormatProgressBar(tool.Progress, 10);
            var icon = tool.Progress > 0 ? "\u25b8" : "\u25c9";

            var line1 = new Label
            {
                Text = $"{icon} {tool.ToolName,-12} [{bar}] {elapsed.TotalSeconds:F1}s",
                X = 0,
                Y = y,
                Width = Dim.Fill()
            };

            var line2 = new Label
            {
                Text = $"  {tool.ArgsSummary}",
                X = 0,
                Y = y + 1,
                Width = Dim.Fill()
            };

            _activeSection.Add(line1, line2);
            y += 3;
        }

        if (_activeTools.Count == 0)
        {
            _activeSection.Add(new Label
            {
                Text = "No active tools",
                X = 0,
                Y = 0,
                Width = Dim.Fill()
            });
        }

        _activeSection.Height = Math.Max(y, 1);
        SetNeedsDraw();
    }

    private void RebuildHistorySection()
    {
        _historySection.RemoveAll();

        var y = 0;
        foreach (var tool in _history)
        {
            var icon = tool.Success ? "\u2713" : "\u2717";
            var suffix = tool.Error is not null ? $" ({tool.Error})" : "";
            var text = $"  {icon} {tool.ToolName,-10} {tool.Duration.TotalSeconds:F1}s{suffix}";

            if (text.Length > 36)
                text = text[..35] + "\u2026";

            _historySection.Add(new Label
            {
                Text = text,
                X = 0,
                Y = y,
                Width = Dim.Fill()
            });
            y++;
        }

        if (_history.Count == 0)
        {
            _historySection.Add(new Label
            {
                Text = "  (none)",
                X = 0,
                Y = 0,
                Width = Dim.Fill()
            });
        }

        _historySection.Height = Math.Max(y, 1);
        SetNeedsDraw();
    }

    private static string FormatProgressBar(float progress, int width)
    {
        var filled = (int)(progress * width);
        var empty = width - filled;
        return new string('\u2588', filled) + new string('\u2500', empty);
    }
}
