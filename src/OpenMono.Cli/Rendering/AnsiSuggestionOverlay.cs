using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Utils;

namespace OpenMono.Rendering;

internal sealed class AnsiSuggestionOverlay(AppConfig config, AnsiPainter painter)
{

    private List<(string Name, string Desc)>? _allCommands;
    private List<(string Name, string Desc)> _filteredCmds = [];
    private int _suggestionIdx = -1;
    private int _suggestionScroll = 0;
    private int _lastDrawnCount = 0;

    private List<string> _atResults = [];
    private int _atSearchIdx = -1;
    private int _atScroll = 0;

    internal const int AtMaxDisplay = 10;

    internal bool CommandSuggestionsVisible => _filteredCmds.Count > 0 && _suggestionIdx >= 0;
    internal bool AtSuggestionsVisible      => _atResults.Count > 0;
    internal int  SuggestionIndex           => _suggestionIdx;
    internal int  AtSearchIndex             => _atSearchIdx;

    internal IReadOnlyList<(string Name, string Desc)> FilteredCommands => _filteredCmds;
    internal IReadOnlyList<string>                     AtResults         => _atResults;

    internal void SetCommands(CommandRegistry registry)
    {
        _allCommands = [];
        foreach (var cmd in registry.All.OrderBy(c => c.Name))
        {
            var name = cmd.Name.TrimStart('/');
            _allCommands.Add(($"/{name}", cmd.Description));
        }
        _allCommands.Add(("/quit", "Exit OpenMono"));
    }

    internal void MoveSuggestionSelection(int delta)
    {
        if (_filteredCmds.Count == 0) return;
        _suggestionIdx = (_suggestionIdx + delta + _filteredCmds.Count) % _filteredCmds.Count;
    }

    internal void MoveAtSelection(int delta)
    {
        if (_atResults.Count == 0) return;
        _atSearchIdx = (_atSearchIdx + delta + _atResults.Count) % _atResults.Count;
    }

    internal string? GetSelectedCommand()
        => _suggestionIdx >= 0 && _suggestionIdx < _filteredCmds.Count
            ? _filteredCmds[_suggestionIdx].Name
            : null;

    internal string? GetSelectedAtResult()
        => _atSearchIdx >= 0 && _atSearchIdx < _atResults.Count
            ? _atResults[_atSearchIdx]
            : null;

    internal static int FindAtStart(string buf, int cursor)
    {
        for (var i = cursor - 1; i >= 0; i--)
        {
            if (buf[i] == '@') return i;
            if (!(char.IsLetterOrDigit(buf[i]) || buf[i] is '/' or '\\' or '.' or '-' or '_'))
                return -1;
        }
        return -1;
    }

    internal void UpdateSuggestions(string text, ref bool visible)
    {
        // Once the user has typed "/model " (command + space), suggest backend-advertised model
        // names instead of commands. Selecting one completes the line to "/model <name>".
        if (text.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            var query  = text["/model ".Length..].TrimStart();
            var models = ModelCatalog.Models;

            _filteredCmds = models
                .Where(m => query.Length == 0 || m.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => ($"/model {m}",
                    string.Equals(m, config.Llm.Model, StringComparison.OrdinalIgnoreCase) ? "current" : ""))
                .ToList();

            if (_filteredCmds.Count == 0)
            {
                if (visible) HideSuggestions(text);
                visible = false;
                _suggestionIdx = -1;
                return;
            }

            _suggestionIdx = 0;
            _suggestionScroll = 0;
            visible = true;
            DrawSuggestions(text);
            return;
        }

        if (_allCommands is null || !text.StartsWith('/'))
        {
            if (visible) { HideSuggestions(text); visible = false; }
            _filteredCmds.Clear();
            _suggestionIdx = -1;
            return;
        }

        _filteredCmds = _allCommands
            .Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_filteredCmds.Count == 0)
        {
            if (visible) HideSuggestions(text);
            visible = false;
            _suggestionIdx = -1;
            return;
        }

        _suggestionIdx = 0;
        _suggestionScroll = 0;
        visible = true;
        DrawSuggestions(text);
    }

    internal void DrawSuggestions(string bgText)
    {
        if (_filteredCmds.Count == 0) return;

        painter.Sz();
        var layout = painter.ComputeLayout(bgText);
        var mainW  = layout.MainW;
        var convH  = layout.ConvH;
        var max    = Math.Min(_filteredCmds.Count, 8);

        if (_suggestionIdx < _suggestionScroll)
            _suggestionScroll = _suggestionIdx;
        else if (_suggestionIdx >= _suggestionScroll + max)
            _suggestionScroll = _suggestionIdx - max + 1;
        _suggestionScroll = Math.Clamp(_suggestionScroll, 0, Math.Max(0, _filteredCmds.Count - max));

        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < _lastDrawnCount - max; i++)
        {
            var row = convH - _lastDrawnCount + i + 1;
            if (row < 1) continue;
            sb.Append($"{AnsiPainter.E}[{row};1H{AnsiPainter.BgMain}{new string(' ', mainW)}{AnsiPainter.R}");
        }

        for (var i = 0; i < max; i++)
        {
            var row = convH - max + i + 1;
            if (row < 1) continue;
            var idx = _suggestionScroll + i;
            var (name, desc) = _filteredCmds[idx];
            sb.Append($"{AnsiPainter.E}[{row};1H");
            if (idx == _suggestionIdx)
                sb.Append(
                    $"{AnsiPainter.BgSugg}{AnsiPainter.Fg}{AnsiPainter.B} {name,-14}{AnsiPainter.R}" +
                    $"{AnsiPainter.BgSugg}{AnsiPainter.Fk} {desc}{AnsiPainter.R}" +
                    $"{AnsiPainter.BgSugg}{AnsiPainter.Pad(Math.Max(0, mainW - 16 - AnsiPainter.VisLen(desc)))}{AnsiPainter.R}");
            else
                sb.Append(
                    $"{AnsiPainter.BgMain}{AnsiPainter.Fk}  {name,-14} {desc}{AnsiPainter.R}" +
                    $"{AnsiPainter.BgMain}{AnsiPainter.Pad(Math.Max(0, mainW - 17 - AnsiPainter.VisLen(desc)))}{AnsiPainter.R}");
        }
        _lastDrawnCount = max;

        var overlayStr = sb.ToString();
        painter.SetSuggestionOverlay(overlayStr);
        painter.Write(overlayStr);
        AnsiPainter.Flush();
    }

    internal void HideSuggestions(string bgText)
    {
        painter.ClearSuggestionOverlay();
        painter.Sz();
        var layout = painter.ComputeLayout(bgText);
        var mainW  = layout.MainW;
        var convH  = layout.ConvH;
        var max    = _lastDrawnCount > 0 ? _lastDrawnCount : Math.Min(_filteredCmds.Count, 8);

        for (var i = 0; i < max; i++)
        {
            var row = convH - max + i + 1;
            if (row < 1) continue;
            painter.MoveTo(1, row);
            painter.Write($"{AnsiPainter.BgMain}{new string(' ', mainW)}{AnsiPainter.R}");
        }

        _filteredCmds.Clear();
        _suggestionIdx = -1;
        _lastDrawnCount = 0;
        AnsiPainter.Flush();
        painter.InvalidateFrameBuffer();
        painter.PaintConvThrottled(force: true);
    }

    internal void UpdateAtSearch(string buf, int cursor, ref bool visible)
    {
        var atPos = FindAtStart(buf, cursor);
        if (atPos < 0)
        {
            if (visible) { HideAtSuggestions(buf); visible = false; }
            return;
        }

        var query      = buf.Substring(atPos + 1, cursor - atPos - 1);
        var newResults = FileSearcher.Search(config.WorkingDirectory, query, 12);
        if (newResults.Count == 0)
        {
            if (visible) { HideAtSuggestions(buf); visible = false; }
            _atResults.Clear();
            _atSearchIdx = -1;
            return;
        }

        _atResults   = newResults;
        _atSearchIdx = 0;
        _atScroll    = 0;
        visible      = true;
        DrawAtSuggestions(buf);
    }

    internal void DrawAtSuggestions(string bgText)
    {
        painter.Sz();
        var layout = painter.ComputeLayout(bgText);
        var mainW  = layout.MainW;
        var convH  = layout.ConvH;

        if (_atSearchIdx < _atScroll)
            _atScroll = _atSearchIdx;
        else if (_atSearchIdx >= _atScroll + AtMaxDisplay)
            _atScroll = _atSearchIdx - AtMaxDisplay + 1;
        _atScroll = Math.Clamp(_atScroll, 0, Math.Max(0, _atResults.Count - AtMaxDisplay));

        var overlay = new System.Text.StringBuilder();
        for (var i = 0; i < AtMaxDisplay; i++)
        {
            var row = convH - AtMaxDisplay + i + 1;
            if (row < 1) continue;
            var idx = _atScroll + i;
            overlay.Append($"{AnsiPainter.E}[{row};1H");
            if (idx < _atResults.Count)
            {
                var rel      = _atResults[idx];
                var fileName = Path.GetFileName(rel);
                var dir      = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                if (idx == _atSearchIdx)
                    overlay.Append(
                        $"{AnsiPainter.BgSugg}{AnsiPainter.Fc}{AnsiPainter.B} @{fileName,-20}{AnsiPainter.R}" +
                        $"{AnsiPainter.BgSugg}{AnsiPainter.Fk} {dir}{AnsiPainter.R}" +
                        $"{AnsiPainter.BgSugg}{AnsiPainter.Pad(Math.Max(0, mainW - 23 - AnsiPainter.VisLen(dir)))}{AnsiPainter.R}");
                else
                    overlay.Append(
                        $"{AnsiPainter.BgMain}{AnsiPainter.Fk}  @{fileName,-20} {dir}{AnsiPainter.R}" +
                        $"{AnsiPainter.BgMain}{AnsiPainter.Pad(Math.Max(0, mainW - 24 - AnsiPainter.VisLen(dir)))}{AnsiPainter.R}");
            }
            else
            {
                overlay.Append($"{AnsiPainter.BgMain}{new string(' ', mainW)}{AnsiPainter.R}");
            }
        }

        var overlayStr = overlay.ToString();
        painter.SetAtOverlay(overlayStr);
        painter.Write(overlayStr);
        AnsiPainter.Flush();
    }

    internal void HideAtSuggestions(string bgText)
    {
        painter.ClearAtOverlay();
        painter.Sz();
        var layout = painter.ComputeLayout(bgText);
        var mainW  = layout.MainW;
        var convH  = layout.ConvH;

        for (var i = 0; i < AtMaxDisplay; i++)
        {
            var row = convH - AtMaxDisplay + i + 1;
            if (row < 1) continue;
            painter.MoveTo(1, row);
            painter.Write($"{AnsiPainter.BgMain}{new string(' ', mainW)}{AnsiPainter.R}");
        }

        _atResults.Clear();
        _atSearchIdx = -1;
        AnsiPainter.Flush();
        painter.InvalidateFrameBuffer();
        painter.PaintConvThrottled(force: true);
    }
}
