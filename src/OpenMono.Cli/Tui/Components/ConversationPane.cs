using OpenMono.Session;
using OpenMono.Tui.Rendering;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public class ConversationPane : FrameView
{
    private readonly VirtualListView _virtualList;
    private int _streamingIndex = -1;
    private bool _autoScroll = true;
    private bool _autoScrollEnabled = true;
    private bool _scrollingProgrammatically;

    public ConversationPane()
    {
        Title = "Conversation";
        CanFocus = true;
        BorderStyle = LineStyle.Rounded;

        _virtualList = new VirtualListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        Add(_virtualList);

        _virtualList.ViewportChanged += OnViewportChanged;
    }

    public bool AutoScrollEnabled
    {
        get => _autoScrollEnabled;
        set
        {
            _autoScrollEnabled = value;
            if (value)
            {
                _autoScroll = true;
                ScrollToBottom();
            }
        }
    }

    public void AppendMessage(Message msg)
    {
        _virtualList.AddEntry(msg);

        if (_autoScroll && _autoScrollEnabled)
            ScrollToBottom();
    }

    public void StreamStart()
    {
        _streamingIndex = _virtualList.BeginStreaming();

        if (_autoScroll && _autoScrollEnabled)
            ScrollToBottom();
    }

    public void StreamToken(string token)
    {
        if (_streamingIndex < 0)
            return;

        _virtualList.AppendStreaming(_streamingIndex, token);

        if (_autoScroll && _autoScrollEnabled)
            ScrollToBottom();
    }

    public void StreamEnd()
    {
        if (_streamingIndex < 0)
            return;

        _virtualList.EndStreaming(_streamingIndex);
        _streamingIndex = -1;

        if (_autoScroll && _autoScrollEnabled)
            ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        var contentHeight = _virtualList.TotalContentHeight;
        var viewportHeight = _virtualList.Viewport.Height;
        if (contentHeight > viewportHeight)
        {
            _scrollingProgrammatically = true;
            var vp = _virtualList.Viewport;
            _virtualList.Viewport = vp with { Y = -(contentHeight - viewportHeight) };
            _scrollingProgrammatically = false;
        }
    }

    private void OnViewportChanged(object? sender, DrawEventArgs e)
    {
        if (_scrollingProgrammatically)
            return;

        var contentHeight = _virtualList.TotalContentHeight;
        var viewportBottom = -_virtualList.Viewport.Y + _virtualList.Viewport.Height;
        _autoScroll = viewportBottom >= contentHeight - 1;
    }
}
