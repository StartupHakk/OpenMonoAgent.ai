using OpenMono.Tui.Rendering;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace OpenMono.Tui.Components;

public class ThinkingPanel : FrameView
{
    private readonly TextView _textView;
    private string _content = "";
    private bool _autoShow;

    public ThinkingPanel()
    {
        Title = "Agent Thinking";
        BorderStyle = LineStyle.Rounded;
        CanFocus = false;
        Visible = false;

        Width = Dim.Percent(40);
        Height = Dim.Percent(30);
        X = Pos.AnchorEnd(0) - Pos.Func(v => v?.Frame.Width ?? 0, this);
        Y = Pos.AnchorEnd(0) - Pos.Func(v => v?.Frame.Height ?? 0, this);

        var theme = ThemeManager.Current;
        SetScheme(new Scheme(new TgAttribute(theme.Muted, theme.Background))
        {
            Normal = new TgAttribute(theme.Muted, theme.Background),
            Focus = new TgAttribute(theme.Muted, theme.Background),
        });

        _textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
        };

        Add(_textView);
    }

    public bool AutoShow
    {
        get => _autoShow;
        set => _autoShow = value;
    }

    public void AppendThinking(string text)
    {
        _content += text;
        _textView.Text = _content;

        if (_autoShow && !Visible)
            Visible = true;

        SetNeedsDraw();
    }

    public void ClearThinking()
    {
        _content = "";
        _textView.Text = "";

        if (_autoShow)
            Visible = false;

        SetNeedsDraw();
    }

    public void ToggleVisibility()
    {
        Visible = !Visible;
        SetNeedsDraw();
    }
}
