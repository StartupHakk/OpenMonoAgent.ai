using System.Drawing;
using OpenMono.Session;
using OpenMono.Tui.Rendering;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace OpenMono.Tui.Components;

internal sealed class MessageEntry
{
    public required string Title { get; init; }
    public required MessageRole Role { get; init; }
    public required string? ToolName { get; init; }
    public required bool IsToolMessage { get; init; }
    public string Text { get; set; } = "";

    public int CachedHeight { get; set; }

    public int CumulativeY { get; set; }
}

internal sealed class PooledBlock
{
    public FrameView Container { get; }
    public Label ContentLabel { get; }
    public int BoundIndex { get; set; } = -1;

    public PooledBlock()
    {
        ContentLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };
        Container = new FrameView
        {
            X = 0,
            Y = 0,
            Width = 10,
            Height = 3,
            CanFocus = false
        };
        Container.Add(ContentLabel);
    }

    public void Bind(MessageEntry entry, int availableWidth)
    {
        var indent = entry.IsToolMessage ? 2 : 0;
        var blockWidth = availableWidth - indent;
        var contentHeight = Math.Max(entry.CachedHeight - 2, 1);

        Container.Title = entry.Title;
        Container.X = indent;
        Container.Y = entry.CumulativeY;
        Container.Width = blockWidth;
        Container.Height = entry.CachedHeight;
        Container.BorderStyle = entry.IsToolMessage ? LineStyle.Single : LineStyle.Rounded;
        Container.Visible = true;

        var scheme = GetScheme(entry.Role);
        Container.SetScheme(scheme);

        ContentLabel.Text = entry.Text;
        ContentLabel.Height = contentHeight;
    }

    public void Unbind()
    {
        BoundIndex = -1;
        Container.Visible = false;
    }

    private static Scheme GetScheme(MessageRole role) => role switch
    {
        MessageRole.User => ThemeManager.Current.MakeRoleScheme(ThemeManager.Current.UserBorder),
        MessageRole.Assistant => ThemeManager.Current.MakeRoleScheme(ThemeManager.Current.AssistantBorder),
        MessageRole.Tool => ThemeManager.Current.MakeRoleScheme(ThemeManager.Current.ToolBorder),
        _ => ThemeManager.Current.MakeRoleScheme(ThemeManager.Current.SystemBorder),
    };
}

public sealed class VirtualListView : View
{
    private readonly List<MessageEntry> _entries = [];
    private readonly List<PooledBlock> _pool = [];
    private readonly HashSet<int> _attachedToView = [];

    private const int BufferRows = 3;

    private int _totalContentHeight;
    private int _lastViewportY;
    private int _lastViewportWidth;

    public VirtualListView()
    {
        CanFocus = true;
        ViewportSettings = ViewportSettingsFlags.AllowNegativeY;
        VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

        ViewportChanged += (_, _) => Reconcile();
        FrameChanged += (_, _) =>
        {
            if (Frame.Size.Width != _lastViewportWidth)
            {
                _lastViewportWidth = Frame.Size.Width;
                RecalculateAllHeights();
                Reconcile();
            }
        };
    }

    public int EntryCount => _entries.Count;
    public int TotalContentHeight => _totalContentHeight;

    public int AddEntry(Message msg)
    {
        var entry = new MessageEntry
        {
            Title = GetTitle(msg),
            Role = msg.Role,
            ToolName = msg.ToolName,
            IsToolMessage = msg.Role == MessageRole.Tool,
            Text = msg.Content ?? "",
        };

        var width = GetAvailableWidth();
        entry.CachedHeight = CalculateHeight(entry, width);
        entry.CumulativeY = _totalContentHeight;

        _entries.Add(entry);
        _totalContentHeight += entry.CachedHeight;

        UpdateContentSize();
        Reconcile();

        return _entries.Count - 1;
    }

    public int BeginStreaming()
    {
        var entry = new MessageEntry
        {
            Title = "Assistant",
            Role = MessageRole.Assistant,
            ToolName = null,
            IsToolMessage = false,
            Text = "",
        };
        entry.CachedHeight = 3;
        entry.CumulativeY = _totalContentHeight;

        _entries.Add(entry);
        _totalContentHeight += entry.CachedHeight;

        UpdateContentSize();
        Reconcile();

        return _entries.Count - 1;
    }

    public void AppendStreaming(int index, string token)
    {
        if (index < 0 || index >= _entries.Count) return;

        var entry = _entries[index];
        entry.Text += token;

        var width = GetAvailableWidth();
        var newHeight = CalculateHeight(entry, width);
        if (newHeight != entry.CachedHeight)
        {
            var delta = newHeight - entry.CachedHeight;
            entry.CachedHeight = newHeight;
            RecalculateCumulativeFrom(index + 1, delta);
            _totalContentHeight += delta;
            UpdateContentSize();
        }

        RefreshVisibleBlock(index, width);
        Reconcile();
    }

    public void EndStreaming(int index)
    {

        if (index < 0 || index >= _entries.Count) return;

        var width = GetAvailableWidth();
        var entry = _entries[index];
        var newHeight = CalculateHeight(entry, width);
        if (newHeight != entry.CachedHeight)
        {
            var delta = newHeight - entry.CachedHeight;
            entry.CachedHeight = newHeight;
            RecalculateCumulativeFrom(index + 1, delta);
            _totalContentHeight += delta;
            UpdateContentSize();
        }

        Reconcile();
    }

    private void Reconcile()
    {
        var viewportY = -Viewport.Y;
        var viewportHeight = Viewport.Height;
        if (viewportHeight <= 0) return;

        var width = GetAvailableWidth();
        var visibleTop = Math.Max(0, viewportY - BufferRows * 3);
        var visibleBottom = viewportY + viewportHeight + BufferRows * 3;

        var firstVisible = FindFirstVisibleEntry(visibleTop);
        var lastVisible = FindLastVisibleEntry(visibleBottom);

        var needed = new HashSet<int>();
        for (var i = firstVisible; i <= lastVisible && i < _entries.Count; i++)
            needed.Add(i);

        foreach (var block in _pool)
        {
            if (block.BoundIndex >= 0 && !needed.Contains(block.BoundIndex))
                block.Unbind();
        }

        foreach (var idx in needed)
        {

            var existing = FindBoundBlock(idx);
            if (existing is not null)
            {

                existing.Container.Y = _entries[idx].CumulativeY;
                continue;
            }

            var block = GetFreeBlock();
            block.BoundIndex = idx;
            block.Bind(_entries[idx], width);

            if (!_attachedToView.Contains(_pool.IndexOf(block)))
            {
                Add(block.Container);
                _attachedToView.Add(_pool.IndexOf(block));
            }
        }

        _lastViewportY = viewportY;
        SetNeedsDraw();
    }

    private void RefreshVisibleBlock(int entryIndex, int width)
    {
        var block = FindBoundBlock(entryIndex);
        if (block is not null)
            block.Bind(_entries[entryIndex], width);
    }

    private PooledBlock GetFreeBlock()
    {
        foreach (var block in _pool)
        {
            if (block.BoundIndex < 0)
                return block;
        }

        var newBlock = new PooledBlock();
        _pool.Add(newBlock);
        return newBlock;
    }

    private PooledBlock? FindBoundBlock(int entryIndex)
    {
        foreach (var block in _pool)
        {
            if (block.BoundIndex == entryIndex)
                return block;
        }
        return null;
    }

    private int GetAvailableWidth()
    {
        return Math.Max(Viewport.Width - 2, 40);
    }

    private static int CalculateHeight(MessageEntry entry, int availableWidth)
    {
        var indent = entry.IsToolMessage ? 2 : 0;
        var blockWidth = availableWidth - indent;
        var textWidth = Math.Max(blockWidth - 4, 20);
        var lines = WrapLineCount(entry.Text, textWidth);
        return Math.Max(lines, 1) + 2;
    }

    private static int WrapLineCount(string text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return 1;

        var lines = 0;
        foreach (var line in text.Split('\n'))
            lines += Math.Max(1, (int)Math.Ceiling((double)line.Length / width));
        return lines;
    }

    private void RecalculateAllHeights()
    {
        var width = GetAvailableWidth();
        _totalContentHeight = 0;
        foreach (var entry in _entries)
        {
            entry.CachedHeight = CalculateHeight(entry, width);
            entry.CumulativeY = _totalContentHeight;
            _totalContentHeight += entry.CachedHeight;
        }
        UpdateContentSize();
    }

    private void RecalculateCumulativeFrom(int startIndex, int delta)
    {
        for (var i = startIndex; i < _entries.Count; i++)
            _entries[i].CumulativeY += delta;
    }

    private int FindFirstVisibleEntry(int y)
    {
        var lo = 0;
        var hi = _entries.Count - 1;
        var result = 0;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var bottom = _entries[mid].CumulativeY + _entries[mid].CachedHeight;
            if (bottom <= y)
            {
                lo = mid + 1;
                result = lo;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Math.Clamp(result, 0, Math.Max(0, _entries.Count - 1));
    }

    private int FindLastVisibleEntry(int y)
    {
        var lo = 0;
        var hi = _entries.Count - 1;
        var result = hi;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_entries[mid].CumulativeY >= y)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return Math.Clamp(result, 0, Math.Max(0, _entries.Count - 1));
    }

    private void UpdateContentSize()
    {
        SetContentSize(new Size(Viewport.Width, _totalContentHeight));
    }

    private static string GetTitle(Message msg) => msg.Role switch
    {
        MessageRole.User => "User",
        MessageRole.Assistant => "Assistant",
        MessageRole.Tool => msg.ToolName ?? "Tool",
        MessageRole.System => "System",
        _ => "Unknown"
    };
}
