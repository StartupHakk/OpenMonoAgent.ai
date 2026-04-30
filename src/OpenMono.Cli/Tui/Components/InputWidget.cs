using System.Collections.ObjectModel;
using OpenMono.Commands;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui.Components;

public class InputWidget : View
{
    private readonly Label _prompt;
    private readonly TextView _editor;
    private readonly FrameView _suggestionsFrame;
    private readonly ListView _suggestionsList;
    private readonly ObservableCollection<string> _suggestionsSource = [];
    private readonly List<(string Name, string Description)> _allCommands;
    private readonly List<(string Name, string Description)> _filteredCommands = [];
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private bool _suggestionsVisible;
    private DateTime _lastCtrlCTime = DateTime.MinValue;

    public event EventHandler<string>? OnSubmit;
    public event EventHandler? OnCancel;

    public InputWidget(CommandRegistry commands)
    {
        Height = Dim.Auto(DimAutoStyle.Content);
        Width = Dim.Fill();
        CanFocus = true;
        BorderStyle = LineStyle.Rounded;

        _allCommands = [];
        foreach (var cmd in commands.All.OrderBy(c => c.Name))
        {
            var name = cmd.Name.TrimStart('/');
            _allCommands.Add(($"/{name}", cmd.Description));
        }
        _allCommands.Add(("/quit", "Exit OpenMono"));

        _prompt = new Label
        {
            Text = "> ",
            X = 0,
            Y = 0,
            Width = 2
        };

        _editor = new TextView
        {
            X = 2,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            WordWrap = false
        };

        _suggestionsFrame = new FrameView
        {
            Title = "Suggestions",
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = 0,
            Visible = false,
            BorderStyle = LineStyle.Single,
            CanFocus = false
        };

        _suggestionsList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false
        };
        _suggestionsList.SetSource(_suggestionsSource);

        _suggestionsFrame.Add(_suggestionsList);
        Add(_prompt, _editor, _suggestionsFrame);

        _editor.ContentsChanged += (_, _) => HandleTextChanged();

        _editor.KeyDown += OnEditorKeyDown;
    }

    public void Focus()
    {
        _editor.SetFocus();
    }

    public void Clear()
    {
        _editor.Text = "";
        HideSuggestions();
    }

    private void OnEditorKeyDown(object? sender, Key e)
    {

        if (e == Key.C.WithCtrl)
        {
            e.Handled = true;
            var now = DateTime.UtcNow;
            var isDouble = (now - _lastCtrlCTime).TotalSeconds <= 1.5;

            if (isDouble)
            {
                OnCancel?.Invoke(this, EventArgs.Empty);
            }
            else if ((_editor.Text?.Length ?? 0) == 0)
            {

                _lastCtrlCTime = now;
                OnSubmit?.Invoke(this, "/clear");
            }
            else
            {
                _lastCtrlCTime = now;
                _editor.Text = "";
                HideSuggestions();
            }
            return;
        }

        if (e == Key.U.WithCtrl)
        {
            e.Handled = true;
            _editor.Text = "";
            HideSuggestions();
            return;
        }

        if (e == Key.W.WithCtrl)
        {
            e.Handled = true;
            var text = _editor.Text ?? "";
            if (text.Length > 0)
            {
                var end = text.Length;
                while (end > 0 && text[end - 1] == ' ') end--;
                while (end > 0 && text[end - 1] != ' ') end--;
                _editor.Text = text[..end];
            }
            return;
        }

        if (e == Key.Enter)
        {
            e.Handled = true;

            var selectedIdx = _suggestionsList.SelectedItem ?? -1;
            if (_suggestionsVisible && selectedIdx >= 0
                && selectedIdx < _filteredCommands.Count)
            {

                var selected = _filteredCommands[selectedIdx].Name;
                _editor.Text = selected;
                HideSuggestions();
                Submit(selected);
            }
            else
            {
                var text = _editor.Text?.Trim() ?? "";
                if (text.Length > 0)
                    Submit(text);
            }
            return;
        }

        if (e == Key.Enter.WithShift)
        {

            var lineCount = (_editor.Text?.Count(c => c == '\n') ?? 0) + 2;
            var newHeight = Math.Min(lineCount, 3);
            _editor.Height = newHeight;
            SetNeedsDraw();
            return;
        }

        if (e == Key.Esc)
        {
            e.Handled = true;
            if (_suggestionsVisible)
            {
                HideSuggestions();
            }
            else
            {
                _editor.Text = "";
            }
            return;
        }

        if (e == Key.Tab)
        {
            e.Handled = true;
            if (_suggestionsVisible && _filteredCommands.Count > 0)
            {
                var idx = (_suggestionsList.SelectedItem ?? -1) >= 0 ? _suggestionsList.SelectedItem!.Value : 0;
                var completed = _filteredCommands[idx].Name;
                _editor.Text = completed;
                HideSuggestions();
            }
            return;
        }

        if (_suggestionsVisible && _filteredCommands.Count > 0)
        {
            if (e == Key.CursorUp)
            {
                e.Handled = true;
                var current = _suggestionsList.SelectedItem ?? 0;
                _suggestionsList.SelectedItem = current <= 0 ? _filteredCommands.Count - 1 : current - 1;
                return;
            }

            if (e == Key.CursorDown)
            {
                e.Handled = true;
                var current = _suggestionsList.SelectedItem ?? 0;
                _suggestionsList.SelectedItem = current >= _filteredCommands.Count - 1 ? 0 : current + 1;
                return;
            }
        }

        if (!_suggestionsVisible && _history.Count > 0)
        {
            if (e == Key.CursorUp)
            {
                e.Handled = true;
                if (_historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                    _editor.Text = _history[_history.Count - 1 - _historyIndex];
                }
                return;
            }

            if (e == Key.CursorDown)
            {
                e.Handled = true;
                if (_historyIndex > 0)
                {
                    _historyIndex--;
                    _editor.Text = _history[_history.Count - 1 - _historyIndex];
                }
                else
                {
                    _historyIndex = -1;
                    _editor.Text = "";
                }
                return;
            }
        }
    }

    private void HandleTextChanged()
    {
        var text = _editor.Text ?? "";

        var lineCount = text.Count(c => c == '\n') + 1;
        _editor.Height = Math.Min(lineCount, 3);

        if (text.StartsWith('/'))
        {
            UpdateSuggestions(text);
        }
        else
        {
            HideSuggestions();
        }
    }

    private void UpdateSuggestions(string typed)
    {
        _filteredCommands.Clear();
        _filteredCommands.AddRange(
            _allCommands.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        );

        if (_filteredCommands.Count == 0)
        {
            HideSuggestions();
            return;
        }

        _suggestionsSource.Clear();
        foreach (var (name, desc) in _filteredCommands)
            _suggestionsSource.Add($"{name,-14} {desc}");

        var visibleCount = Math.Min(_filteredCommands.Count, 8);
        _suggestionsFrame.Height = visibleCount + 2;
        _suggestionsFrame.Visible = true;
        _suggestionsVisible = true;
        _suggestionsList.SelectedItem = 0;

        SetNeedsDraw();
    }

    private void HideSuggestions()
    {
        _suggestionsFrame.Visible = false;
        _suggestionsFrame.Height = 0;
        _suggestionsVisible = false;
        _filteredCommands.Clear();
        _suggestionsSource.Clear();
        SetNeedsDraw();
    }

    private void Submit(string text)
    {
        if (_history.Count == 0 || _history[^1] != text)
            _history.Add(text);

        _historyIndex = -1;
        _editor.Text = "";
        _editor.Height = 1;
        HideSuggestions();

        OnSubmit?.Invoke(this, text);
    }
}
