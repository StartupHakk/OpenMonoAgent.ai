using System.Diagnostics;
using System.Text;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Permissions;
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

    private (int scroll, string? paste) TryReadEscapeSequence()
    {
        var seq = new StringBuilder(32);
        while (seq.Length < 64)
        {
            var waited = 0;
            while (!Console.KeyAvailable && waited < 10) { Thread.Sleep(1); waited++; }
            if (!Console.KeyAvailable) break;
            var k = terminal.TryReadKey();
            if (k is null) break;
            seq.Append(k.Value.KeyChar);
            var s = seq.ToString();

            if (s == "[200~")
                return (0, ReadBracketedPasteContent());

            if (k.Value.KeyChar is 'M' or 'm')
            {
                if (s.StartsWith("[<") && s.Length >= 4)
                {
                    var termIdx = s.Length - 1;
                    var inner = s[2..termIdx];
                    var parts = inner.Split(';');
                    if (parts.Length == 3 && int.TryParse(parts[0], out var btn))
                    {
                        if (btn == 64) return (+1, null);
                        if (btn == 65) return (-1, null);
                    }
                }
                break;
            }

            if ((k.Value.KeyChar >= 'A' && k.Value.KeyChar <= 'Z') ||
                (k.Value.KeyChar >= 'a' && k.Value.KeyChar <= 'z') ||
                k.Value.KeyChar == '~')
                break;
        }
        return (0, null);
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

    internal string BgInputText => _bgInputBuf.ToString();
    internal bool IsBackgroundInputActive => _bgInputActive;

    internal void StartBackgroundInput()
    {
        StopBackgroundInput();
        _bgInputBuf.Clear();
        _bgInputActive = true;
        painter.DrawInputText("", 0);
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
        while (_bgInputActive)
        {
            var result = terminal.TryReadKey();
            if (result is null) { Thread.Sleep(50); continue; }
            var k = result.Value;
            if (!_bgInputActive) break;

            if (k.Key == ConsoleKey.Escape)
            {
                if (!Console.KeyAvailable) Thread.Sleep(12);
                if (Console.KeyAvailable)
                {
                    var (scroll, paste) = TryReadEscapeSequence();
                    if (paste is not null)
                    {
                        _bgInputBuf.Append(paste.Replace("\r\n", "\n").Replace('\r', '\n'));
                        if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                    }
                    else if (scroll > 0) { painter.ScrollBy(+3); painter.Paint(); }
                    else if (scroll < 0) { painter.ScrollBy(-3); painter.Paint(); }
                    continue;
                }
                CurrentTurnCts?.Cancel();
                continue;
            }

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
                    CurrentTurnCts?.Cancel();
                    painter.ShowCtrlCBanner();
                }
                continue;
            }

            if (k.Key == ConsoleKey.U && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
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
                    if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                }
                continue;
            }

            if (k.Key == ConsoleKey.PageUp)
            {
                painter.ScrollBy(+3);
                painter.Paint();
                continue;
            }

            if (k.Key == ConsoleKey.PageDown)
            {
                painter.ScrollBy(-3);
                painter.Paint();
                continue;
            }

            if (k.Key == ConsoleKey.Home && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { painter.ScrollToTop(); painter.Paint(); continue; }

            if (k.Key == ConsoleKey.End && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            { painter.ScrollToBottom(); painter.Paint(); continue; }

            if (k.Key == ConsoleKey.Enter)
            {
                var text = _bgInputBuf.ToString().Trim();
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
                    if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                }
                continue;
            }

            if (k.Key == ConsoleKey.V && k.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                var p = ReadClipboard();
                if (p is not null)
                {
                    _bgInputBuf.Append(p.Replace("\r\n", "\n").Replace('\r', '\n'));
                    if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
                }
                continue;
            }

            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            {
                _bgInputBuf.Append(k.KeyChar);
                if (!painter.PaintInProgress) painter.DrawInputText(_bgInputBuf.ToString(), _bgInputBuf.Length);
            }
        }
    }

    public void EnableCommandSuggestions(CommandRegistry registry)
        => suggestions.SetCommands(registry);

    public string ReadInput() => ReadInputCore(interactive: false);

    public string? ShowCommandPicker(CommandRegistry registry) => null;

    public Task<string> AskUserAsync(string question, CancellationToken ct)
    {
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
        StartBackgroundInput();
        return Task.FromResult(ans);
    }

    public Task<PermissionResponse> AskPermissionAsync(string tool, string summary, CancellationToken ct)
    {
        StopBackgroundInput();
        painter.AddMessage(new AnsiPainter.Msg("sys",
            $"{AnsiPainter.Fy}▶ Permission: {tool}{AnsiPainter.R}\n{summary}"));
        painter.PaintConvThrottled(force: true);

        painter.Sz();
        var maxSummaryLen   = painter.ComputeLayout("").MainW - 4;
        var truncatedSummary = summary.Length > maxSummaryLen
            ? summary[..(maxSummaryLen - 3)] + "..."
            : summary;

        painter.PaintActionLane(
            $"{AnsiPainter.Fy}{AnsiPainter.B}▸ Permission required: {tool}{AnsiPainter.R}",
            $"{AnsiPainter.Fw}{truncatedSummary}{AnsiPainter.R}",
            $"  {AnsiPainter.B}{AnsiPainter.Fg}[y]{AnsiPainter.R}{AnsiPainter.BgInput} Allow  " +
            $"{AnsiPainter.B}{AnsiPainter.Fy}[n]{AnsiPainter.R}{AnsiPainter.BgInput} Deny  " +
            $"{AnsiPainter.B}{AnsiPainter.Fc}[a]{AnsiPainter.R}{AnsiPainter.BgInput} Allow all  " +
            $"{AnsiPainter.B}{AnsiPainter.Fr}[!]{AnsiPainter.R}{AnsiPainter.BgInput} Deny all"
        );

        var response = ReadPermissionKey();
        painter.PaintActionLane("", "", "");
        painter.Paint();
        StartBackgroundInput();
        return Task.FromResult(response);
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
                    painter.PaintConvThrottled(force: true);
                }

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
                    if (!Console.KeyAvailable) Thread.Sleep(12);
                    if (Console.KeyAvailable)
                    {
                        var (scroll, paste) = TryReadEscapeSequence();
                        if (paste is not null)
                        {
                            var c = paste.Replace("\r\n", "\n").Replace('\r', '\n');
                            buf.Insert(cur, c); cur += c.Length;
                            var ps = buf.ToString();
                            suggestions.UpdateSuggestions(ps, ref sugVis);
                            if (!sugVis) suggestions.UpdateAtSearch(ps, cur, ref atVis);
                            painter.DrawInputText(ps, cur);
                        }
                        else if (scroll > 0) { painter.ScrollBy(+3); painter.Paint(); }
                        else if (scroll < 0) { painter.ScrollBy(-3); painter.Paint(); }
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

                if (k.Key == ConsoleKey.LeftArrow)  { if (cur > 0)            cur--; painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.RightArrow) { if (cur < buf.Length)   cur++; painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.Home)        { cur = 0;               painter.DrawInputText(buf.ToString(), cur); continue; }
                if (k.Key == ConsoleKey.End)         { cur = buf.Length;      painter.DrawInputText(buf.ToString(), cur); continue; }

                if (k.Key == ConsoleKey.PageUp)
                {
                    painter.ScrollBy(+3);
                    painter.PaintConvThrottled(force: true);
                    continue;
                }
                if (k.Key == ConsoleKey.PageDown)
                {
                    painter.ScrollBy(-3);
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
            Console.TreatControlCAsInput = prev;
            painter.Write($"{AnsiPainter.E}[?2004l");
            AnsiPainter.Flush();
        }
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
                    if (!Console.KeyAvailable) Thread.Sleep(12);
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
