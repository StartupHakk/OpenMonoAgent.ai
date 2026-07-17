using System.Diagnostics;
using System.Text;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Utils;

namespace OpenMono.Rendering;

internal sealed class AnsiInputReader(
    AnsiPainter           painter,
    AnsiSuggestionOverlay suggestions,
    ITerminal             terminal) : IInputReader
{

    private readonly StringBuilder _bgInputBuf = new();
    private volatile bool _bgInputActive;
    private Thread? _bgInputThread;

    private readonly List<string> _inputHistory = [];
    private int _historyIndex = -1;

    private DateTime _lastCtrlCTime = DateTime.MinValue;

    private (int scroll, string? paste, int wordMove, int lineMove) TryReadEscapeSequence()
    {
        var seq = new StringBuilder(32);
        while (seq.Length < 64)
        {
            var waited = 0;
            while (!Console.KeyAvailable && waited < 10) { Thread.Sleep(1); waited++; }
            if (!Console.KeyAvailable) break;
            var k = terminal.TryReadKey();
            if (k is null) break;
            var ch = k.Value.KeyChar;
            seq.Append(ch);
            var s = seq.ToString();

            if (s == "[200~")
                return (0, ReadBracketedPasteContent(), 0, 0);

            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '~')
            {
                if (s == "O" && Console.KeyAvailable)
                    continue;

                if ((ch == 'M' || ch == 'm') && s.Length > 3 && s[0] == '[' && s[1] == '<')
                {
                    var body  = s.Substring(2, s.Length - 3);
                    var parts = body.Split(';');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[0], out var cb) &&
                        int.TryParse(parts[1], out var cx) &&
                        int.TryParse(parts[2], out var cy))
                    {
                        if ((cb & 0x40) != 0)
                        {
                            var dir = cb & 0x3;
                            if (dir == 0) return (+2, null, 0, 0);
                            if (dir == 1) return (-2, null, 0, 0);
                            return (0, null, 0, 0);
                        }

                        var screenRow = cy - 1;
                        var screenCol = cx - 2;
                        if (ch == 'M' && (cb & 0x20) == 0 && (cb & 0x03) == 0)
                            painter.MouseSelectStart(screenRow, screenCol);
                        else if (ch == 'M' && (cb & 0x20) != 0)
                            painter.MouseSelectExtend(screenRow, screenCol);
                        else if (ch == 'm')
                        {
                            var sel = painter.MouseSelectCommit();
                            if (!string.IsNullOrEmpty(sel))
                            {
                                WriteClipboard(sel);
                                var n = sel.Length;
                                painter.ShowToast($"Copied {n} character{(n == 1 ? "" : "s")} to clipboard");
                            }
                        }
                    }
                    return (0, null, 0, 0);
                }

                return s switch
                {
                    "b" or "[1;3D" or "[1;5D" => (0, null, -1,  0),
                    "f" or "[1;3C" or "[1;5C" => (0, null, +1,  0),
                    "[1;9D" or "[H" or "OH"   => (0, null,  0, -1),
                    "[1;9C" or "[F" or "OF"   => (0, null,  0, +1),
                    "[5~"                      => (+1, null,  0,  0),
                    "[6~"                      => (-1, null,  0,  0),
                    "[1;2A"                    => (+2, null,  0,  0),
                    "[1;2B"                    => (-2, null,  0,  0),
                    _                          => (0, null,  0,  0),
                };
            }
        }
        return (0, null, 0, 0);
    }

    private void ApplyScroll(int scroll)
    {
        switch (scroll)
        {
            case  1: painter.ScrollPageUp();   break;
            case -1: painter.ScrollPageDown(); break;
            case  2: painter.ScrollBy(+3);     break;
            case -2: painter.ScrollBy(-3);     break;
            default: return;
        }
        painter.Paint();
    }

    private static int MoveWordBackward(string text, int cursor)
    {
        var pos = cursor;
        while (pos > 0 && text[pos - 1] == ' ') pos--;
        while (pos > 0 && text[pos - 1] != ' ') pos--;
        return pos;
    }

    private static int MoveWordForward(string text, int cursor)
    {
        var pos = cursor;
        while (pos < text.Length && text[pos] != ' ') pos++;
        while (pos < text.Length && text[pos] == ' ') pos++;
        return pos;
    }

    private string ReadBracketedPasteContent()
    {
        var sb = new StringBuilder(1024);
        const string end = "\x1b[201~";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var waited = 0;
            while (!Console.KeyAvailable && waited < 200) { Thread.Sleep(1); waited++; }
            if (!Console.KeyAvailable) break;
            var k = terminal.TryReadKey();
            if (k is null) break;

            var ch = k.Value.Key == ConsoleKey.Escape ? '\x1b'
                   : k.Value.Key == ConsoleKey.Enter  ? '\n'
                   : k.Value.KeyChar;
            if (ch == '\0') continue;

            sb.Append(ch);

            if (ch == '~' && sb.Length >= end.Length &&
                sb.ToString(sb.Length - end.Length, end.Length) == end)
            {
                sb.Remove(sb.Length - end.Length, end.Length);
                break;
            }
        }
        return sb.ToString();
    }

    internal Action OnSafeExit { private get; set; } = () => { };

    internal CancellationTokenSource? CurrentTurnCts { get; set; }

    private Func<string>? _liveMainInput;
    internal string BgInputText => _liveMainInput?.Invoke() ?? _bgInputBuf.ToString();
    internal bool IsBackgroundInputActive => _bgInputActive;

    internal void StartBackgroundInput(string? preserveText = null)
    {
        StopBackgroundInput();
        if (preserveText is not null)
        {
            _bgInputBuf.Clear();
            _bgInputBuf.Append(preserveText);
        }
        _bgInputActive = true;
        var displayText = _bgInputBuf.ToString();
        painter.DrawInputText(displayText, displayText.Length);
        painter.Write($"{AnsiPainter.E}[?25h");
        painter.Write($"{AnsiPainter.E}[?2004h");
        AnsiPainter.Flush();
        Console.TreatControlCAsInput = true;
        _bgInputThread = new Thread(BgInputLoop) { IsBackground = true, Name = "BgInput" };
        _bgInputThread.Start();
    }

    internal void StopBackgroundInput()
    {
        _bgInputActive = false;
        _bgInputThread = null;
        painter.Write($"{AnsiPainter.E}[?2004l");
        AnsiPainter.Flush();
    }

    private void BgInputLoop()
    {
        var sugVis = false;

        while (_bgInputActive)
        {
            var result = terminal.TryReadKey();
            if (result is null) { Thread.Sleep(50); continue; }
            var k = result.Value;
            if (!_bgInputActive) break;

            if (k.Key == ConsoleKey.Escape)
            {
                if (!Console.KeyAvailable) { var ms = 0; while (!Console.KeyAvailable && ms < 50) { Thread.Sleep(1); ms++; } }
                if (Console.KeyAvailable)
                {
                    var (scroll, paste, _, _) = TryReadEscapeSequence();
                    if (paste is not null)
                    {
                        if (sugVis) { suggestions.HideSuggestions(_bgInputBuf.ToString()); sugVis = false; }
                        _bgInputBuf.Append(paste.Replace("\r\n", "\n").Replace('\r', '\n'));
                        if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                    }
                    else if (scroll != 0) { ApplyScroll(scroll); }
                    continue;
                }
                if (sugVis) { suggestions.HideSuggestions(_bgInputBuf.ToString()); sugVis = false; continue; }
                CurrentTurnCts?.Cancel();
                continue;
            }

            if (k.Key == ConsoleKey.PageUp)   { painter.ScrollPageUp(); painter.Paint(); continue; }
            if (k.Key == ConsoleKey.PageDown) { painter.ScrollPageDown(); painter.Paint(); continue; }

            if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                var now = DateTime.UtcNow;
                var isDouble = (now - _lastCtrlCTime).TotalSeconds <= 1.5;
                _lastCtrlCTime = now;

                if (isDouble)
                {
                    _bgInputActive = false;
                    ProcessWatchdog.ScheduleHardKill();
                    OnSafeExit();
                    Environment.Exit(0);
                }
                else
                {
                    if (sugVis) { suggestions.HideSuggestions(_bgInputBuf.ToString()); sugVis = false; }
                    CurrentTurnCts?.Cancel();
                    painter.ShowCtrlCBanner();
                }
                continue;
            }

            if (k.Key == ConsoleKey.U && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (sugVis) { suggestions.HideSuggestions(_bgInputBuf.ToString()); sugVis = false; }
                _bgInputBuf.Clear();
                if (!painter.PaintInProgress) painter.DrawInputText("", 0);
                continue;
            }

            if (k.Key == ConsoleKey.W && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (_bgInputBuf.Length > 0)
                {
                    var s   = _bgInputBuf.ToString();
                    var end = s.Length;
                    while (end > 0 && s[end - 1] == ' ') end--;
                    while (end > 0 && s[end - 1] != ' ') end--;
                    _bgInputBuf.Clear();
                    _bgInputBuf.Append(s[..end]);
                    var ws = _bgInputBuf.ToString();
                    suggestions.UpdateSuggestions(ws, ref sugVis);
                    if (!painter.PaintInProgress) painter.DrawInputText(ws, _bgInputBuf.Length);
                }
                continue;
            }

            if (k.Key == ConsoleKey.Home && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { painter.ScrollToTop(); painter.Paint(); continue; }

            if (k.Key == ConsoleKey.End && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { painter.ScrollToBottom(); painter.Paint(); continue; }

            if (k.Key == ConsoleKey.UpArrow && sugVis && suggestions.FilteredCommands.Count > 0)
            { suggestions.MoveSuggestionSelection(-1); suggestions.DrawSuggestions(_bgInputBuf.ToString()); continue; }
            if (k.Key == ConsoleKey.DownArrow && sugVis && suggestions.FilteredCommands.Count > 0)
            { suggestions.MoveSuggestionSelection(+1); suggestions.DrawSuggestions(_bgInputBuf.ToString()); continue; }

            if (k.Key == ConsoleKey.Tab && sugVis && suggestions.FilteredCommands.Count > 0)
            {
                var idx  = suggestions.SuggestionIndex >= 0 ? suggestions.SuggestionIndex : 0;
                var comp = suggestions.FilteredCommands[idx].Name;
                _bgInputBuf.Clear();
                _bgInputBuf.Append(comp);
                suggestions.HideSuggestions(comp);
                sugVis = false;
                painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                continue;
            }

            if (k.Key == ConsoleKey.Enter)
            {
                var text = _bgInputBuf.ToString().Trim();

                if (sugVis && suggestions.SuggestionIndex >= 0 &&
                    suggestions.SuggestionIndex < suggestions.FilteredCommands.Count)
                {
                    text = suggestions.FilteredCommands[suggestions.SuggestionIndex].Name;
                    _bgInputBuf.Clear();
                    _bgInputBuf.Append(text);
                }
                suggestions.HideSuggestions(_bgInputBuf.ToString());
                sugVis = false;

                if (text is "exit" or "quit" or "q" or "/quit" or "/exit" or "/q")
                {
                    CurrentTurnCts?.Cancel();
                    _bgInputBuf.Clear();
                    if (!painter.PaintInProgress) painter.DrawInputText("", 0);
                    continue;
                }
                if (text.Length > 0)
                    painter.EnqueueUserMessage(text);
                _bgInputBuf.Clear();
                if (!painter.PaintInProgress) painter.DrawInputText("", 0);
                continue;
            }

            if (k.Key == ConsoleKey.Backspace)
            {
                if (_bgInputBuf.Length > 0)
                {
                    _bgInputBuf.Remove(_bgInputBuf.Length - 1, 1);
                    var bs = _bgInputBuf.ToString();
                    suggestions.UpdateSuggestions(bs, ref sugVis);
                    if (!painter.PaintInProgress) painter.DrawInputText(bs, _bgInputBuf.Length);
                }
                continue;
            }

            if (k.Key == ConsoleKey.V && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                var p = ReadClipboard();
                if (p is not null)
                {
                    if (sugVis) { suggestions.HideSuggestions(_bgInputBuf.ToString()); sugVis = false; }
                    _bgInputBuf.Append(p.Replace("\r\n", "\n").Replace('\r', '\n'));
                    if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                }
                continue;
            }

            if (k.Modifiers.HasFlag(ConsoleModifiers.Alt))
                continue;

            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            {
                _bgInputBuf.Append(k.KeyChar);
                var cs = _bgInputBuf.ToString();
                suggestions.UpdateSuggestions(cs, ref sugVis);
                if (!painter.PaintInProgress) painter.DrawInputText(cs, _bgInputBuf.Length);
            }
        }
    }

    public void EnableCommandSuggestions(CommandRegistry registry)
        => suggestions.SetCommands(registry);

    public string ReadInput() => ReadInputCore(interactive: false);

    public string? ShowCommandPicker(CommandRegistry registry) => null;

    public Task<string> AskUserAsync(string question, CancellationToken ct)
    {
        var saved = _bgInputBuf.ToString();
        painter.AddMessage(new AnsiPainter.Msg("sys", $"? {question}"));
        StopBackgroundInput();
        painter.PaintActionLane(
            $"{AnsiPainter.Fc}{AnsiPainter.B}? {question}{AnsiPainter.R}",
            "",
            $"  {AnsiPainter.Fk}Type your answer and press Enter{AnsiPainter.R}"
        );
        painter.PaintConvThrottled(force: true);

        var ans = ReadInputCore(interactive: true);
        painter.AddMessage(new AnsiPainter.Msg("user", ans));
        painter.PaintActionLane("", "", "");
        painter.Paint();
        StartBackgroundInput(saved);
        return Task.FromResult(ans);
    }

    public Task<PermissionResponse> AskPermissionAsync(string tool, string summary, CancellationToken ct)
    {
        var saved = _bgInputBuf.ToString();
        StopBackgroundInput();

        // Alert the user in case the terminal/window isn't focused. Inside the
        // container this rings the host terminal (bell + OSC 9 banner); on the
        // host it raises a native OS notification.
        Utils.DesktopNotifier.Alert(
            "OpenMono — permission needed",
            $"The agent needs your permission to run {tool}.");

        painter.AddMessage(new AnsiPainter.Msg("sys",
            $"{AnsiPainter.Fy}▶ Permission: {tool}{AnsiPainter.R}\n{summary}"));
        painter.PaintConvThrottled(force: true);

        painter.Sz();
        var maxSummaryLen   = painter.ComputeLayout("").MainW - 4;
        var truncatedSummary = summary.Length > maxSummaryLen
            ? summary[..(maxSummaryLen - 3)] + "..."
            : summary;

        painter.PaintPermissionLane(
            $"{AnsiPainter.Fy}{AnsiPainter.B}▸ Permission required: {tool}{AnsiPainter.R}",
            $"{AnsiPainter.Fw}{truncatedSummary}{AnsiPainter.R}",
            $"  {AnsiPainter.B}{AnsiPainter.Fg}[y]{AnsiPainter.R}{AnsiPainter.BgInput}  Allow",
            $"  {AnsiPainter.B}{AnsiPainter.Fy}[n]{AnsiPainter.R}{AnsiPainter.BgInput}  Deny",
            $"  {AnsiPainter.B}{AnsiPainter.Fc}[a]{AnsiPainter.R}{AnsiPainter.BgInput}  Allow all",
            $"  {AnsiPainter.B}{AnsiPainter.Fr}[!]{AnsiPainter.R}{AnsiPainter.BgInput}  Deny all"
        );

        PermissionResponse response;
        try { response = ReadPermissionKey(); }
        finally { painter.ClearLane(); }
        painter.Paint();
        StartBackgroundInput(saved);
        return Task.FromResult(response);
    }

    public Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct)
    {
        var saved = _bgInputBuf.ToString();
        StopBackgroundInput();
        painter.AddMessage(new AnsiPainter.Msg("sys",
            $"{AnsiPainter.Fy}▶ Playbook approval: {plan.PlaybookName}{AnsiPainter.R}"));

        var stepsStr = string.Join(", ", plan.Steps.Select(s => s.Id));
        var toolsStr = string.Join(", ", plan.Tools.Select(t => t.Name));

        painter.Sz();
        var maxLineLen = painter.ComputeLayout("").MainW - 4;
        var truncatedSteps = stepsStr.Length > maxLineLen
            ? stepsStr[..(maxLineLen - 3)] + "..."
            : stepsStr;
        var truncatedTools = toolsStr.Length > maxLineLen
            ? toolsStr[..(maxLineLen - 3)] + "..."
            : toolsStr;

        painter.PaintPermissionLane(
            $"{AnsiPainter.Fy}{AnsiPainter.B}▸ Approve playbook: {plan.PlaybookName}{AnsiPainter.R}",
            $"{AnsiPainter.Fw}Steps: {truncatedSteps}\nTools: {truncatedTools}{AnsiPainter.R}",
            $"  {AnsiPainter.B}{AnsiPainter.Fg}[y]{AnsiPainter.R}{AnsiPainter.BgInput}  Allow",
            $"  {AnsiPainter.B}{AnsiPainter.Fy}[n]{AnsiPainter.R}{AnsiPainter.BgInput}  Deny",
            "",
            ""
        );

        bool approved;
        try
        {
            while (true)
            {
                var result = terminal.TryReadKey();
                if (result is null) { Thread.Sleep(20); continue; }
                var k = result.Value;

                if (k.KeyChar is 'y' or 'Y') { approved = true; break; }
                if (k.KeyChar is 'n' or 'N') { approved = false; break; }
            }
        }
        finally { painter.ClearLane(); }

        painter.Paint();
        StartBackgroundInput(saved);
        return Task.FromResult(approved);
    }

    private string ReadInputCore(bool interactive)
    {
        if (!interactive)
        {
            var queued = painter.DequeueMessage();
            if (queued is not null) return queued;
        }

        painter.Sz();
        painter.Write($"{AnsiPainter.E}[?25h");
        painter.Write($"{AnsiPainter.E}[?2004h");

        var buf = new StringBuilder();
        if (_bgInputBuf.Length > 0)
        {
            buf.Append(_bgInputBuf);
            _bgInputBuf.Clear();
        }
        var cur = buf.Length;
        _liveMainInput = () => buf.ToString();

        painter.DrawInputText(buf.ToString(), cur);
        AnsiPainter.Flush();

        var sugVis          = false;
        var atVis           = false;
        var ctrlCBannerShown = false;

        var prev = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                var result = terminal.TryReadKey();
                if (result is null)
                {
                    Thread.Sleep(20);
                    continue;
                }
                var k = result.Value;

                if (ctrlCBannerShown)
                {
                    ctrlCBannerShown = false;
                    painter.InvalidateFrameBuffer();
                    painter.PaintConvThrottled(force: true);
                }

                if (k.Key == ConsoleKey.PageUp)   { painter.ScrollPageUp(); painter.Paint(); continue; }
                if (k.Key == ConsoleKey.PageDown) { painter.ScrollPageDown(); painter.Paint(); continue; }

                if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    var now = DateTime.UtcNow;
                    var isDouble = (now - _lastCtrlCTime).TotalSeconds <= 1.5;

                    if (isDouble)
                    {
                        suggestions.HideSuggestions(buf.ToString());
                        ProcessWatchdog.ScheduleHardKill();
                        OnSafeExit();
                        Environment.Exit(0);
                    }

                    _lastCtrlCTime = now;
                    buf.Clear(); cur = 0;
                    suggestions.HideSuggestions(""); sugVis = false;
                    painter.DrawInputText("", 0);
                    painter.ShowCtrlCBanner(); ctrlCBannerShown = true;
                    continue;
                }

                if ((k.Key == ConsoleKey.V && k.Modifiers.HasFlag(ConsoleModifiers.Control)) ||
                    (k.Key == ConsoleKey.Insert && k.Modifiers.HasFlag(ConsoleModifiers.Shift)))
                {
                    var p = ReadClipboard();
                    if (p is not null)
                    {
                        var c = p.Replace("\r\n", "\n").Replace('\r', '\n');
                        buf.Insert(cur, c); cur += c.Length;
                        var ps = buf.ToString();
                        suggestions.UpdateSuggestions(ps, ref sugVis);
                        if (!sugVis) suggestions.UpdateAtSearch(ps, cur, ref atVis);
                        painter.DrawInputText(ps, cur);
                    }
                    continue;
                }

                if (k.Key == ConsoleKey.P && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    if (atVis) { suggestions.HideAtSuggestions(buf.ToString()); atVis = false; }
                    buf.Clear(); buf.Append('/'); cur = 1;
                    suggestions.UpdateSuggestions("/", ref sugVis);
                    painter.DrawInputText("/", 1);
                    continue;
                }

                if (k.Key == ConsoleKey.Escape)
                {
                    if (!Console.KeyAvailable) { var ms = 0; while (!Console.KeyAvailable && ms < 50) { Thread.Sleep(1); ms++; } }
                    if (Console.KeyAvailable)
                    {
                        var (scroll, paste, wordMove, lineMove) = TryReadEscapeSequence();
                        if (paste is not null)
                        {
                            var c = paste.Replace("\r\n", "\n").Replace('\r', '\n');
                            buf.Insert(cur, c); cur += c.Length;
                            var ps = buf.ToString();
                            suggestions.UpdateSuggestions(ps, ref sugVis);
                            if (!sugVis) suggestions.UpdateAtSearch(ps, cur, ref atVis);
                            painter.DrawInputText(ps, cur);
                        }
                        else if (wordMove < 0) { cur = MoveWordBackward(buf.ToString(), cur); painter.DrawInputText(buf.ToString(), cur); }
                        else if (wordMove > 0) { cur = MoveWordForward(buf.ToString(), cur);  painter.DrawInputText(buf.ToString(), cur); }
                        else if (lineMove < 0) { cur = 0;           painter.DrawInputText(buf.ToString(), cur); }
                        else if (lineMove > 0) { cur = buf.Length;  painter.DrawInputText(buf.ToString(), cur); }
                        else if (scroll != 0) { ApplyScroll(scroll); }
                        continue;
                    }
                    if (atVis) { suggestions.HideAtSuggestions(buf.ToString()); atVis = false; continue; }
                    if (sugVis) { suggestions.HideSuggestions(buf.ToString()); sugVis = false; continue; }
                    if (CurrentTurnCts is { } cts && !cts.IsCancellationRequested) { cts.Cancel(); continue; }
                    buf.Clear(); cur = 0; painter.DrawInputText("", 0); continue;
                }

                if (k.Key == ConsoleKey.Enter)
                {
                    if (atVis && suggestions.AtSearchIndex >= 0 &&
                        suggestions.AtSearchIndex < suggestions.AtResults.Count)
                    {
                        var atPos = AnsiSuggestionOverlay.FindAtStart(buf.ToString(), cur);
                        if (atPos >= 0)
                        {
                            var path = suggestions.AtResults[suggestions.AtSearchIndex];
                            buf.Remove(atPos, cur - atPos);
                            buf.Insert(atPos, "@" + path + " ");
                            cur = atPos + path.Length + 2;
                        }
                        suggestions.HideAtSuggestions(buf.ToString()); atVis = false;
                        painter.DrawInputText(buf.ToString(), cur);
                        continue;
                    }

                    if (sugVis && suggestions.SuggestionIndex >= 0 &&
                        suggestions.SuggestionIndex < suggestions.FilteredCommands.Count)
                    {
                        var sel = suggestions.FilteredCommands[suggestions.SuggestionIndex].Name;
                        buf.Clear(); buf.Append(sel); cur = sel.Length;
                    }

                    suggestions.HideSuggestions(buf.ToString());
                    suggestions.HideAtSuggestions(buf.ToString()); atVis = false;
                    var text = buf.ToString();
                    painter.Write($"{AnsiPainter.E}[?25l{AnsiPainter.R}");
                    AnsiPainter.Flush();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (!interactive && (painter.IsStreaming || CurrentTurnCts is not null))
                    {
                        if (text.Trim() is "/quit" or "/exit" or "/q" or "quit" or "exit" or "q")
                        {
                            CurrentTurnCts?.Cancel();
                            buf.Clear(); cur = 0;
                            painter.Write($"{AnsiPainter.E}[?25h");
                            painter.DrawInputText("", 0);
                            continue;
                        }
                        painter.EnqueueUserMessage(text);
                        buf.Clear(); cur = 0;
                        painter.Write($"{AnsiPainter.E}[?25h");
                        painter.DrawInputText("", 0);
                        continue;
                    }

                    if (_inputHistory.Count == 0 || _inputHistory[^1] != text)
                        _inputHistory.Add(text);
                    _historyIndex = -1;
                    return text;
                }

                if (k.Key == ConsoleKey.Tab && atVis && suggestions.AtResults.Count > 0)
                {
                    var atPos = AnsiSuggestionOverlay.FindAtStart(buf.ToString(), cur);
                    if (atPos >= 0)
                    {
                        var path = suggestions.AtResults[suggestions.AtSearchIndex >= 0 ? suggestions.AtSearchIndex : 0];
                        buf.Remove(atPos, cur - atPos);
                        buf.Insert(atPos, "@" + path + " ");
                        cur = atPos + path.Length + 2;
                    }
                    suggestions.HideAtSuggestions(buf.ToString()); atVis = false;
                    painter.DrawInputText(buf.ToString(), cur);
                    continue;
                }

                if (k.Key == ConsoleKey.Tab && sugVis && suggestions.FilteredCommands.Count > 0)
                {
                    var idx  = suggestions.SuggestionIndex >= 0 ? suggestions.SuggestionIndex : 0;
                    var comp = suggestions.FilteredCommands[idx].Name;
                    buf.Clear(); buf.Append(comp); cur = comp.Length;
                    suggestions.UpdateSuggestions(comp, ref sugVis);
                    painter.DrawInputText(buf.ToString(), cur);
                    continue;
                }

                if (k.Key == ConsoleKey.UpArrow && atVis && suggestions.AtResults.Count > 0)
                { suggestions.MoveAtSelection(-1); suggestions.DrawAtSuggestions(buf.ToString()); continue; }
                if (k.Key == ConsoleKey.DownArrow && atVis && suggestions.AtResults.Count > 0)
                { suggestions.MoveAtSelection(+1); suggestions.DrawAtSuggestions(buf.ToString()); continue; }

                if (k.Key == ConsoleKey.UpArrow && sugVis && suggestions.FilteredCommands.Count > 0)
                { suggestions.MoveSuggestionSelection(-1); suggestions.DrawSuggestions(buf.ToString()); continue; }
                if (k.Key == ConsoleKey.DownArrow && sugVis && suggestions.FilteredCommands.Count > 0)
                { suggestions.MoveSuggestionSelection(+1); suggestions.DrawSuggestions(buf.ToString()); continue; }

                if (k.Key == ConsoleKey.UpArrow && !sugVis && !atVis && _inputHistory.Count > 0)
                {
                    if (_historyIndex < _inputHistory.Count - 1) _historyIndex++;
                    var entry = _inputHistory[_inputHistory.Count - 1 - _historyIndex];
                    buf.Clear(); buf.Append(entry); cur = buf.Length;
                    painter.DrawInputText(buf.ToString(), cur);
                    continue;
                }
                if (k.Key == ConsoleKey.DownArrow && !sugVis && !atVis && _historyIndex >= 0)
                {
                    _historyIndex--;
                    var entry = _historyIndex < 0 ? "" : _inputHistory[_inputHistory.Count - 1 - _historyIndex];
                    buf.Clear(); buf.Append(entry); cur = buf.Length;
                    painter.DrawInputText(buf.ToString(), cur);
                    continue;
                }

                if (k.Key == ConsoleKey.B && k.Modifiers.HasFlag(ConsoleModifiers.Alt))
                { cur = MoveWordBackward(buf.ToString(), cur); painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.F && k.Modifiers.HasFlag(ConsoleModifiers.Alt))
                { cur = MoveWordForward(buf.ToString(), cur); painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.LeftArrow && k.Modifiers.HasFlag(ConsoleModifiers.Alt))
                { cur = 0; painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.RightArrow && k.Modifiers.HasFlag(ConsoleModifiers.Alt))
                { cur = buf.Length; painter.DrawInputText(buf.ToString(), cur); continue; }

                if (k.Key == ConsoleKey.LeftArrow && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                { cur = MoveWordBackward(buf.ToString(), cur); painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.RightArrow && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                { cur = MoveWordForward(buf.ToString(), cur); painter.DrawInputText(buf.ToString(), cur); continue; }

                if (k.Key == ConsoleKey.LeftArrow)  { if (cur > 0)            cur--; painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.RightArrow) { if (cur < buf.Length)   cur++; painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.Home)        { cur = 0;               painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.End)         { cur = buf.Length;      painter.DrawInputText(buf.ToString(), cur); continue; }

                if (k.Key == ConsoleKey.PageUp)
                {
                    painter.ScrollPageUp();
                    painter.PaintConvThrottled(force: true);
                    continue;
                }
                if (k.Key == ConsoleKey.PageDown)
                {
                    painter.ScrollPageDown();
                    painter.PaintConvThrottled(force: true);
                    continue;
                }

                if (k.Key == ConsoleKey.Home && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                { painter.ScrollToTop(); painter.PaintConvThrottled(force: true); continue; }
                if (k.Key == ConsoleKey.End && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                { painter.ScrollToBottom(); painter.PaintConvThrottled(force: true); continue; }

                if (k.Key == ConsoleKey.Backspace)
                {
                    if (cur > 0)
                    {
                        buf.Remove(cur - 1, 1); cur--;
                        var bs = buf.ToString();
                        suggestions.UpdateSuggestions(bs, ref sugVis);
                        if (!sugVis) suggestions.UpdateAtSearch(bs, cur, ref atVis);
                        painter.DrawInputText(bs, cur);
                    }
                    continue;
                }

                if (k.Key == ConsoleKey.Delete)
                {
                    if (cur < buf.Length)
                    {
                        buf.Remove(cur, 1);
                        var ds = buf.ToString();
                        suggestions.UpdateSuggestions(ds, ref sugVis);
                        if (!sugVis) suggestions.UpdateAtSearch(ds, cur, ref atVis);
                        painter.DrawInputText(ds, cur);
                    }
                    continue;
                }

                if (k.Key == ConsoleKey.U && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    buf.Clear(); cur = 0;
                    suggestions.HideSuggestions(""); sugVis = false;
                    painter.DrawInputText("", 0);
                    continue;
                }

                if (k.Key == ConsoleKey.W && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    if (buf.Length > 0 && cur > 0)
                    {
                        var end = cur;
                        while (end > 0 && buf[end - 1] == ' ') end--;
                        while (end > 0 && buf[end - 1] != ' ') end--;
                        buf.Remove(end, cur - end);
                        cur = end;
                        suggestions.UpdateSuggestions(buf.ToString(), ref sugVis);
                        painter.DrawInputText(buf.ToString(), cur);
                    }
                    continue;
                }

                if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
                {
                    buf.Insert(cur, k.KeyChar); cur++;
                    var cs = buf.ToString();
                    suggestions.UpdateSuggestions(cs, ref sugVis);
                    if (!sugVis) suggestions.UpdateAtSearch(cs, cur, ref atVis);
                    painter.DrawInputText(cs, cur);
                }
            }
        }
        finally
        {
            _liveMainInput = null;
            Console.TreatControlCAsInput = prev;
            painter.Write($"{AnsiPainter.E}[?2004l");
            AnsiPainter.Flush();
        }
    }

    internal char ReadMenuKey(params char[] allowed)
    {
        var prev = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                var result = terminal.TryReadKey();
                if (result is null) { Thread.Sleep(20); continue; }
                var k = result.Value;

                if (k.Key == ConsoleKey.Escape)
                {
                    if (!Console.KeyAvailable) { var ms = 0; while (!Console.KeyAvailable && ms < 50) { Thread.Sleep(1); ms++; } }
                    if (Console.KeyAvailable) { TryReadEscapeSequence(); continue; }
                    return '\0';
                }

                if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    ProcessWatchdog.ScheduleHardKill();
                    OnSafeExit();
                    Environment.Exit(0);
                }

                var ch = char.ToLowerInvariant(k.KeyChar);
                foreach (var a in allowed)
                    if (ch == char.ToLowerInvariant(a)) return ch;
            }
        }
        finally { Console.TreatControlCAsInput = prev; }
    }

    private PermissionResponse ReadPermissionKey()
    {
        var prev = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                var result = terminal.TryReadKey();
                if (result is null) { Thread.Sleep(20); continue; }
                var k = result.Value;

                if (k.KeyChar is 'y' or 'Y') return PermissionResponse.Allow;
                if (k.KeyChar is 'n' or 'N') return PermissionResponse.Deny;
                if (k.KeyChar is 'a' or 'A') return PermissionResponse.AllowAll;
                if (k.KeyChar == '!')         return PermissionResponse.DenyAll;
                if (k.Key == ConsoleKey.Escape)
                {
                    if (!Console.KeyAvailable) { var ms = 0; while (!Console.KeyAvailable && ms < 50) { Thread.Sleep(1); ms++; } }
                    if (Console.KeyAvailable) { TryReadEscapeSequence(); continue; }
                    return PermissionResponse.Deny;
                }

                if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    ProcessWatchdog.ScheduleHardKill();
                    OnSafeExit();
                    Environment.Exit(0);
                }
            }
        }
        finally { Console.TreatControlCAsInput = prev; }
    }

    private static void WriteClipboard(string text)
    {
        try
        {
            ProcessStartInfo psi;
            if (File.Exists("/usr/bin/pbcopy"))
                psi = new("pbcopy") { RedirectStandardInput = true, UseShellExecute = false };
            else
                psi = new("xclip", "-selection clipboard") { RedirectStandardInput = true, UseShellExecute = false };

            var p = Process.Start(psi);
            if (p is null) return;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(2000);
        }
        catch { }
    }

    private static string? ReadClipboard()
    {
        try
        {
            ProcessStartInfo psi;
            if (File.Exists("/usr/bin/pbpaste"))
                psi = new("pbpaste") { RedirectStandardOutput = true, UseShellExecute = false };
            else
                psi = new("xclip", "-selection clipboard -o") { RedirectStandardOutput = true, UseShellExecute = false };

            var p = Process.Start(psi);
            if (p is null) return null;
            var t = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return t;
        }
        catch { return null; }
    }
}
