using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using OpenMono.Config;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Rendering;

internal sealed partial class AnsiPainter(AppConfig config, SessionState session, ITerminal terminal)
{

    internal const string E    = "\x1b";
    internal const string R    = "\x1b[0m";
    internal const string B    = "\x1b[1m";
    internal const string DM   = "\x1b[2m";
    internal const string IT   = "\x1b[3m";
    internal const string Fr   = "\x1b[31m";
    internal const string Fg   = "\x1b[32m";
    internal const string Fy   = "\x1b[33m";
    internal const string Fb   = "\x1b[38;2;163;255;102m";
    internal const string Fc   = "\x1b[36m";
    internal const string Fw   = "\x1b[37m";
    internal const string Fk   = "\x1b[90m";
    internal const string Fbb  = "\x1b[38;2;163;255;102m";
    internal const string BgMain   = "\x1b[49m";
    internal const string BgInput  = "\x1b[49m";
    internal const string BgStatus = "\x1b[49m";
    internal const string BgSide   = "\x1b[49m";
    internal const string BgSugg   = "\x1b[49m";

    internal const int MaxCachedLines           = 5000;
    internal const int TrimThreshold            = 6000;
    internal const long PaintIntervalTicks      = 50  * TimeSpan.TicksPerMillisecond;
    internal const long TokenPaintIntervalTicks = 500 * TimeSpan.TicksPerMillisecond;
    internal const long HeartbeatIntervalTicks  = 500 * TimeSpan.TicksPerMillisecond;
    internal const int PaintFailureCircuitBreaker = 10;

    private enum PaintKind { Conv, Full, Input, Sidebar }
    private readonly record struct PaintRequest(PaintKind Kind, string? InputText = null, int Cursor = 0);

    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private static readonly string[] DotsFrames       = { ".", "..", "...", "....", ".....", "....", "...", ".." };
    private static readonly string[] HeartbeatFrames  = ["◐", "◓", "◑", "◒"];

    private readonly List<Msg> _messages = [];
    private readonly object _messagesLock = new();
    private string _lastUserText = "";

    private readonly Queue<string> _messageQueue = new();
    private readonly object _queueLock = new();
    private readonly object _writeLock = new();

    private readonly StringBuilder _streamBuf = new();
    private readonly object _streamLock = new();
    private volatile bool _streaming;
    private volatile string? _toolProgress;
    private int _toolProgressFrame;
    private int _chunks;
    private double _lastTokSec;
    private readonly Stopwatch _turnTimer = new();

    private readonly double[] _tokSecHistory = new double[30];
    private int _tokSecHistoryIdx;
    private long _lastSampleTick;
    private double _windowMax;
    private double _windowAvg;
    private readonly Queue<long> _chunkTicks = new();

    private int _tw;
    private int _th;
    private int _sideW;

    private sealed class ThinkingStream
    {
        public string Mode = "";
        public string WaitingLabel = "Thinking";
        public int Frame;
        public readonly StringBuilder Buffer = new();
        public readonly object BufferLock = new();
        public bool Collapsed;
        public int CollapseChars;
        public long LastActivityTick;
    }
    private const string MainAgentKey = "";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ThinkingStream> _thinkingStreams = new();
    private System.Threading.Timer? _thinkingTimer;
    private readonly object _thinkingTimerLock = new();

    private int _heartbeatFrame;
    private long _lastHeartbeatTick;

    private volatile bool _ctrlCBannerVisible;
    private System.Threading.Timer? _ctrlCBannerTimer;

    private volatile int _contextWarningPct;

    // "Update available" banner text, supplied by the host launcher via env when a
    // newer version of OpenMono is on the tracked branch. Null = up to date / unknown.
    private readonly string? _updateNotice =
        Environment.GetEnvironmentVariable("OPENMONO_UPDATE_NOTICE") is { Length: > 0 } n ? n : null;

    private string[]? _prevConvFrame;
    private string[]? _prevSideFrame;
    private int _prevFrameWidth;
    private int _prevFrameHeight;
    private int _prevInputContentRows = 1;

    private readonly Channel<PaintRequest> _paintChannel = Channel.CreateUnbounded<PaintRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private Thread? _paintThread;
    private volatile bool _paintActive;
    private volatile bool _paintInProgress;
    private CancellationTokenSource? _paintCts;

    private volatile bool _laneActive;
    private string _laneOverlay = "";

    private volatile bool _atOverlayActive;
    private string _atOverlay = "";

    private volatile bool _suggestionOverlayActive;
    private string _suggestionOverlay = "";

    private readonly List<string> _cachedLines = [];
    private int _cachedMsgCount;
    private int _cachedWidth;
    private int _trimmedLineCount;

    private long _lastPaintTick;
    private long _lastTokenPaintTick;

    private int _scrollOffset;
    private bool _autoScroll = true;

    private int _selAnchorRow = -1;
    private int _selAnchorCol;
    private int _selCursorRow = -1;
    private int _selCursorCol;
    private bool _selDragged;
    private string[] _rowPlainText = [];
    private string? _toast;
    private DateTime _toastExpiry;
    private int _inputCursor;

    private Func<string> _getBgInput = () => "";
    private Func<bool> _isTurnActive = () => false;

    internal void SetBgInputProvider(Func<string> getter) => _getBgInput = getter;
    internal void SetTurnActiveProvider(Func<bool> getter) => _isTurnActive = getter;

    internal bool Verbose { get; set; }

    internal bool PaintInProgress => _paintInProgress;
    internal bool IsStreaming => _streaming;

    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiPattern();
    private static readonly Regex AnsiRe = AnsiPattern();

    [GeneratedRegex(@"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeaderPattern();
    private static readonly Regex HunkHeaderRe = HunkHeaderPattern();

    internal record TuiLayout(int MainW, int ConvH, int InputH, int FirstContentRow, int TabRow, int StatusRow);

    internal TuiLayout ComputeLayout(string bgInputText)
    {
        Sz();
        var mainW    = Math.Max(1, _tw - _sideW);
        var inputH   = InputContentRows(bgInputText, mainW) + 2;
        var convH    = Math.Max(0, _th - inputH - 2);
        var tabRow   = convH + inputH;
        var statusRow = _th;
        var firstContentRow = Math.Max(1, _th - InputContentRows(bgInputText, mainW) - 2);
        return new TuiLayout(mainW, convH, inputH, firstContentRow, tabRow, statusRow);
    }

    internal int InputContentRows(string text, int mainW)
        => Math.Clamp(WrapInput(text, InputWrapWidth(mainW)).Length, 1, 5);

    internal int InputWrapWidth(int mainW) => Math.Max(1, (int)(mainW * 0.95) - 2);

    internal static string[] WrapInput(string text, int wrapW)
    {
        if (text.Length == 0) return [""];
        var logicalLines = text.Count(c => c == '\n') + 1;
        if (logicalLines >= 4)
        {
            var indicator = $"[{logicalLines} Lines Copied]";
            var lastNl = text.LastIndexOf('\n');
            var tail = lastNl >= 0 ? text[(lastNl + 1)..] : "";
            if (tail.Length == 0) return [indicator];
            var rows = new List<string> { indicator };
            for (var pos = 0; pos < tail.Length; pos += wrapW)
                rows.Add(tail.Substring(pos, Math.Min(wrapW, tail.Length - pos)));
            if (rows.Count > 5)
            {
                var truncated = new string[5];
                truncated[0] = indicator;
                var skip = rows.Count - 4;
                for (var i = 0; i < 4; i++) truncated[i + 1] = rows[skip + i];
                return truncated;
            }
            return [.. rows];
        }
        var lines = new List<string>();
        foreach (var segment in text.Split('\n'))
        {
            if (segment.Length == 0) { lines.Add(""); continue; }
            for (var pos = 0; pos < segment.Length; pos += wrapW)
                lines.Add(segment.Substring(pos, Math.Min(wrapW, segment.Length - pos)));
        }
        if (lines.Count >= 4)
            return [$"[{lines.Count} Lines Copied]"];
        return lines.Count == 0 ? [""] : [.. lines];
    }

    internal static List<string> Wrap(string text, int w)
    {
        if (w <= 0) return [""];
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var stripped = AnsiRe.Replace(raw, "");
            if (stripped.Length <= w)
            {
                result.Add(raw);
                continue;
            }

            var pos = 0;
            while (pos < stripped.Length)
            {
                var remaining = stripped.Length - pos;
                if (remaining <= w)
                {
                    result.Add(stripped[pos..]);
                    break;
                }

                var end = pos + w;
                var searchLen = Math.Min(w, stripped.Length - pos);
                var breakAt = stripped.LastIndexOf(' ', end - 1, searchLen);
                if (breakAt > pos)
                {
                    result.Add(stripped[pos..breakAt]);
                    pos = breakAt + 1;
                }
                else
                {
                    result.Add(stripped[pos..end]);
                    pos = end;
                }
            }
        }
        return result;
    }

    internal static string PadR(string ansi, int w)
    {
        var vis = VisLen(ansi);
        return vis >= w ? ansi : ansi + new string(' ', Math.Max(0, w - vis));
    }

    internal static string Pad(int n) => n > 0 ? new string(' ', n) : "";

    internal static int VisLen(string ansi) => AnsiRe.Replace(ansi, "").Length;

    internal static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    internal void Sz()
    {
        int newW, newH;
        try
        {
            newW = terminal.WindowWidth;
            newH = terminal.WindowHeight;
        }
        catch
        {
            newW = 120;
            newH = 40;
        }

        if (newW < 40) newW = 40;
        if (newH < 10) newH = 10;

        if (newW != _prevFrameWidth || newH != _prevFrameHeight)
            InvalidateFrameBuffer();

        _tw = newW;
        _th = newH;

        if (config.ShowDetail)
        {
            var preferred = Math.Clamp(_tw / 4, 20, 35);
            _sideW = Math.Min(preferred, (int)(_tw * 0.4));
        }
        else
        {
            _sideW = 0;
        }
    }

    internal static void Flush() => Console.Out.Flush();

    internal void Write(string s) => W(s);

    internal void MoveTo(int col, int row)
        => terminal.WriteAsync($"\x1b[{row};{col}H").GetAwaiter().GetResult();

    internal int GetMaxScrollOffset()
    {
        var convH = _th - 4 - 1 - 1;
        return Math.Max(0, _cachedLines.Count - convH);
    }

    internal int ConvHeight => Math.Max(1, _th - 4 - 1 - 1);

    internal void ScrollBy(int delta)
    {
        if (delta > 0)
        {
            _scrollOffset = Math.Min(_scrollOffset + delta, GetMaxScrollOffset());
            _autoScroll = false;
        }
        else
        {
            _scrollOffset = Math.Max(0, _scrollOffset + delta);
            if (_scrollOffset == 0) _autoScroll = true;
        }
    }

    internal void ScrollPageUp()   => ScrollBy(Math.Max(1, ConvHeight - 2));
    internal void ScrollPageDown() => ScrollBy(-Math.Max(1, ConvHeight - 2));

    internal void ScrollToTop()
    {
        _scrollOffset = GetMaxScrollOffset();
        _autoScroll = false;
    }

    internal void ScrollToBottom()
    {
        _scrollOffset = 0;
        _autoScroll = true;
    }

    internal void MouseSelectStart(int screenRow, int screenCol)
    {
        if (screenRow < 0 || screenRow >= ConvHeight)
        {
            ClearSelection();
            return;
        }
        _selAnchorRow = screenRow;
        _selAnchorCol = Math.Max(0, screenCol);
        _selCursorRow = screenRow;
        _selCursorCol = Math.Max(0, screenCol);
        _selDragged = false;
        PaintConvThrottled(force: true);
    }

    internal void MouseSelectExtend(int screenRow, int screenCol)
    {
        if (_selAnchorRow < 0) return;
        _selCursorRow = Math.Clamp(screenRow, 0, ConvHeight - 1);
        _selCursorCol = Math.Max(0, screenCol);
        _selDragged = true;
        PaintConvThrottled(force: true);
    }

    private (int sr, int sc, int er, int ec) NormalizedSelection()
    {
        var anchorFirst = _selAnchorRow < _selCursorRow ||
            (_selAnchorRow == _selCursorRow && _selAnchorCol <= _selCursorCol);
        return anchorFirst
            ? (_selAnchorRow, _selAnchorCol, _selCursorRow, _selCursorCol)
            : (_selCursorRow, _selCursorCol, _selAnchorRow, _selAnchorCol);
    }

    internal string? MouseSelectCommit()
    {
        if (_selAnchorRow < 0 || !_selDragged)
        {
            ClearSelection();
            return null;
        }

        var (sr, sc, er, ec) = NormalizedSelection();
        var sb = new StringBuilder();
        for (var r = sr; r <= er; r++)
        {
            var line = r < _rowPlainText.Length ? _rowPlainText[r] ?? "" : "";
            var from = r == sr ? Math.Clamp(sc, 0, line.Length) : 0;
            var to   = r == er ? Math.Clamp(ec, 0, line.Length) : line.Length;
            if (to > from) sb.Append(line, from, to - from);
            if (r < er) sb.Append('\n');
        }

        ClearSelection();
        var text = sb.ToString();
        return text.Length == 0 ? null : text;
    }

    private void ClearSelection()
    {
        _selAnchorRow = -1;
        _selCursorRow = -1;
        _selDragged = false;
        PaintConvThrottled(force: true);
    }

    internal void ShowToast(string message)
    {
        _toast = message;
        _toastExpiry = DateTime.UtcNow.AddMilliseconds(2200);
        Paint();
        var expiry = _toastExpiry;
        _ = Task.Delay(2300).ContinueWith(_ =>
        {
            if (_toast is not null && DateTime.UtcNow >= expiry)
            {
                _toast = null;
                Paint();
            }
        });
    }

    internal void Paint()
    {
        if (_paintActive)
        {
            _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Full));
            return;
        }
        lock (_writeLock) { DoPaintFull(); }
    }

    internal void PaintConvThrottled(bool force = false)
    {
        if (!force)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _lastPaintTick < PaintIntervalTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
                return;
            _lastPaintTick = now;
        }
        PaintConvDirect();
    }

    internal void DrawInputText(string text, int cursor = -1)
    {
        var resolvedCursor = cursor >= 0 ? cursor : text.Length;
        if (_paintActive)
        {
            _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Input, text, resolvedCursor));
            return;
        }
        lock (_writeLock) { DoDrawInputText(text, resolvedCursor); }
    }

    internal void PaintActionLane(string line1, string line2, string line3)
    {
        Sz();
        var w = _tw - _sideW;
        var sb = new StringBuilder(512);

        var row1 = Math.Max(1, _th - 3);
        var row2 = Math.Max(1, _th - 2);

        sb.Append($"{E}[{row1};1H");
        sb.Append($"{BgInput} {PadR(line1, Math.Max(0, w - 2))}{R}");

        sb.Append($"{E}[{row2};1H");
        sb.Append($"{BgInput} {PadR(line2, Math.Max(0, w - 2))}{R}");

        var tabRow = Math.Max(1, _th - 1);
        sb.Append($"{E}[{tabRow};1H");
        sb.Append($"{BgInput} {PadR(line3, Math.Max(0, w - 2))}{R}");

        lock (_writeLock)
        {
            W(sb.ToString());
            Flush();
        }
    }

    internal void PaintPermissionLane(string title, string summary, string opt1, string opt2, string opt3, string opt4)
    {
        Sz();
        var w  = _tw - _sideW;
        var sb = new StringBuilder(512);

        void Row(int offset, string text)
        {
            sb.Append($"{E}[{Math.Max(1, _th - offset)};1H");
            sb.Append($"{BgInput} {PadR(text, Math.Max(0, w - 2))}{R}");
        }

        Row(6, title);
        Row(5, summary);
        Row(4, opt1);
        Row(3, opt2);
        Row(2, opt3);
        Row(1, opt4);

        _laneOverlay = sb.ToString();
        _laneActive  = true;
        lock (_writeLock) { W(_laneOverlay); Flush(); }
    }

    internal void ClearLane()
    {
        _laneActive  = false;
        _laneOverlay = "";
    }

    private void AppendLaneOverlay(StringBuilder sb)
    {
        if (_laneActive) sb.Append(_laneOverlay);
    }

    internal void SetAtOverlay(string overlay)
    {
        _atOverlay = overlay;
        _atOverlayActive = true;
    }

    internal void ClearAtOverlay()
    {
        _atOverlayActive = false;
        _atOverlay = "";
    }

    private void AppendAtOverlay(StringBuilder sb)
    {
        if (_atOverlayActive) sb.Append(_atOverlay);
    }

    internal void SetSuggestionOverlay(string overlay)
    {
        _suggestionOverlay = overlay;
        _suggestionOverlayActive = true;
    }

    internal void ClearSuggestionOverlay()
    {
        _suggestionOverlayActive = false;
        _suggestionOverlay = "";
    }

    private void AppendSuggestionOverlay(StringBuilder sb)
    {
        if (_suggestionOverlayActive) sb.Append(_suggestionOverlay);
    }

    internal void ShowCtrlCBanner()
    {
        _ctrlCBannerVisible = true;
        _ctrlCBannerTimer?.Dispose();
        _ctrlCBannerTimer = new System.Threading.Timer(_ =>
        {
            _ctrlCBannerVisible = false;
            _ctrlCBannerTimer = null;
            InvalidateFrameBuffer();
            PaintConvThrottled(force: true);
        }, null, dueTime: 2000, period: System.Threading.Timeout.Infinite);
        PaintConvThrottled(force: true);
    }

    internal void InvalidateCache()
    {
        _cachedLines.Clear();
        _cachedMsgCount = 0;
        _trimmedLineCount = 0;
        InvalidateFrameBuffer();
    }

    internal void InvalidateFrameBuffer()
    {
        _prevConvFrame = null;
        _prevSideFrame = null;
        _prevInputContentRows = 0;
    }

    internal void StartPaintThread()
    {
        if (_paintActive) return;
        _paintCts = new CancellationTokenSource();
        _paintActive = true;
        _paintThread = new Thread(PaintLoop) { IsBackground = true, Name = "PaintLoop" };
        _paintThread.Start();
    }

    internal void StopPaintThread()
    {
        _paintActive = false;
        try { _paintCts?.Cancel(); } catch { }
        _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Conv));
        _paintThread = null;
        while (_paintChannel.Reader.TryRead(out _)) { }
        _paintCts?.Dispose();
        _paintCts = null;
    }

    internal void OnTokensUpdated()
    {
        var tracker = session.Meta.TokenTracker;
        var ctx = config.Llm.ContextSize;
        if (ctx > 0)
        {
            var lastPrompt = tracker?.LastPromptTokens ?? 0;
            var estimated = lastPrompt > 0
                ? lastPrompt
                : session.Messages.Sum(m => (m.Content?.Length ?? 0) + 20) / 4;
            var pct = (int)((double)estimated / ctx * 100);
            _contextWarningPct = pct >= 80 ? pct : 0;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastTokenPaintTick < TokenPaintIntervalTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
            return;
        _lastTokenPaintTick = now;

        if (_paintActive)
        {
            _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Sidebar));
            return;
        }
        lock (_writeLock) { DoPaintSidebar(); }
    }

    internal void AddMessage(Msg m)
    {
        lock (_messagesLock)
        {
            _messages.Add(m);
            if (m.Role == "user") _lastUserText = m.Text;
        }
    }

    internal string? DequeueMessage()
    {
        lock (_queueLock)
            return _messageQueue.Count > 0 ? _messageQueue.Dequeue() : null;
    }

    internal int QueuedCount
    {
        get { lock (_queueLock) return _messageQueue.Count; }
    }

    internal void EnqueueUserMessage(string text)
    {
        lock (_queueLock)
        {
            if (_messageQueue.Count < 2)
            {
                _messageQueue.Enqueue(text);
                AddMessage(new Msg("sys", $"⏳ Queued: {text}"));
            }
            else
            {
                AddMessage(new Msg("sys", "⚠ Queue full (max 2)"));
            }
        }
        PaintConvThrottled(force: true);
    }

    internal void StartAssistantResponse()
    {
        ClearThinking();
        _streaming = true;
        _autoScroll = true;
        _scrollOffset = 0;
        lock (_streamLock) { _streamBuf.Clear(); }
        _chunks = 0;
        _lastTokSec = 0;
        _turnTimer.Restart();
        _lastPaintTick = 0;
        Array.Clear(_tokSecHistory);
        _tokSecHistoryIdx = 0;
        _lastSampleTick = 0;
        _chunkTicks.Clear();
        Volatile.Write(ref _windowMax, 0);
        Volatile.Write(ref _windowAvg, 0);
    }

    internal void StreamText(string text)
    {
        lock (_streamLock) { _streamBuf.Append(text); _chunks++; }

        var now = Stopwatch.GetTimestamp();

        _chunkTicks.Enqueue(now);
        var cutoff = now - 3L * Stopwatch.Frequency;
        while (_chunkTicks.Count > 1 && _chunkTicks.Peek() < cutoff)
            _chunkTicks.Dequeue();

        if (_chunkTicks.Count >= 2)
        {
            var windowSec = (now - _chunkTicks.Peek()) / (double)Stopwatch.Frequency;
            _lastTokSec = windowSec > 0 ? (_chunkTicks.Count - 1) / windowSec : 0;
        }
        else if (_turnTimer.Elapsed.TotalSeconds > 0.2)
        {
            _lastTokSec = _chunks / _turnTimer.Elapsed.TotalSeconds;
        }

        if (now - _lastSampleTick > Stopwatch.Frequency)
        {
            _tokSecHistory[_tokSecHistoryIdx % 30] = _lastTokSec;
            _tokSecHistoryIdx++;
            _lastSampleTick = now;
            ComputeWindowStats();
        }

        if (now - _lastPaintTick > PaintIntervalTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
        {
            _lastPaintTick = now;
            _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Conv));
        }
    }

    internal void EndAssistantResponse(TurnMetrics? metrics)
    {
        _turnTimer.Stop();
        _streaming = false;
        _toolProgress = null;
        ComputeWindowStats();
        string finalText;
        lock (_streamLock) { finalText = _streamBuf.ToString(); _streamBuf.Clear(); }
        string footer;
        if (metrics is { PromptTokens: > 0 } m)
        {
            var genTime = m.TotalElapsed - m.TimeToFirstToken;
            // Prefer llama.cpp's server-reported decode rate; fall back to wall-clock if absent.
            var genTps = m.GenTokensPerSecond > 0
                ? m.GenTokensPerSecond
                : (genTime.TotalSeconds > 0.001 ? m.CompletionTokens / genTime.TotalSeconds : 0);
            footer = $"TTFT {m.TimeToFirstToken.TotalSeconds:F1}s · gen {genTps:F0}/s · {m.CompletionTokens} tok · {m.TotalElapsed.TotalSeconds:F1}s";
        }
        else
        {
            var sec = _turnTimer.Elapsed.TotalSeconds;
            footer = $"{sec:F1}s";
        }
        AddMessage(new Msg("assistant", finalText)
        {
            Footer = footer
        });
        _autoScroll = true;
        _scrollOffset = 0;
        Paint();
    }

    internal void AppendThinking(string text) => AppendThinking(text, null);

    internal void AppendThinking(string text, string? agentLabel)
    {
        var key = agentLabel ?? MainAgentKey;
        var stream = _thinkingStreams.GetOrAdd(key, _ => new ThinkingStream());
        var wasActive = stream.Mode is "Thinking" or "Waiting";
        lock (stream.BufferLock) { stream.Buffer.Append(text); }
        stream.Mode = "Thinking";
        stream.Collapsed = false;
        System.Threading.Interlocked.Exchange(ref stream.LastActivityTick, DateTime.UtcNow.Ticks);
        if (!wasActive) { _autoScroll = true; _scrollOffset = 0; }
        EnsureThinkingTimer();
        PaintConvThrottled(force: false);
    }

    internal void CollapseThinking(int charCount) => CollapseThinking(charCount, null);

    internal void CollapseThinking(int charCount, string? agentLabel)
    {
        var key = agentLabel ?? MainAgentKey;
        if (!_thinkingStreams.TryGetValue(key, out var stream)) return;
        stream.Collapsed = true;
        stream.CollapseChars = charCount;
        stream.Mode = "";
        lock (stream.BufferLock) { stream.Buffer.Clear(); }
        StopThinkingTimerIfIdle();
        PaintConvThrottled(force: true);
    }

    internal void ShowWaitingIndicator(string? label = null) => ShowWaitingIndicator(label, null);

    internal void ShowWaitingIndicator(string? label, string? agentLabel)
    {
        var key = agentLabel ?? MainAgentKey;
        var stream = _thinkingStreams.GetOrAdd(key, _ => new ThinkingStream());
        stream.WaitingLabel = string.IsNullOrEmpty(label) ? "Thinking" : label;
        if (stream.Mode != "Waiting")
        {
            stream.Mode = "Waiting";
            stream.Frame = 0;
            _autoScroll = true;
            _scrollOffset = 0;
        }
        EnsureThinkingTimer();
        PaintConvThrottled(force: true);
    }

    internal void ClearWaitingIndicator() => ClearWaitingIndicator(null);

    internal void ClearWaitingIndicator(string? agentLabel)
    {
        var key = agentLabel ?? MainAgentKey;
        if (!_thinkingStreams.TryGetValue(key, out var stream)) return;
        if (stream.Mode == "Waiting")
        {
            stream.WaitingLabel = "Thinking";
            ClearThinkingForKey(key);
        }
    }

    internal void ShowToolProgress(string label)
    {
        _toolProgress = label;
        System.Threading.Interlocked.Increment(ref _toolProgressFrame);
        PaintConvThrottled(force: false);
    }

    internal void ClearToolProgress()
    {
        if (_toolProgress is null) return;
        _toolProgress = null;
        PaintConvThrottled(force: true);
    }

    private void EnsureThinkingTimer()
    {
        lock (_thinkingTimerLock)
        {
            if (_thinkingTimer is not null) return;
            _thinkingTimer = new System.Threading.Timer(_ =>
            {
                foreach (var s in _thinkingStreams.Values)
                    System.Threading.Interlocked.Increment(ref s.Frame);
                if (_paintActive)
                    _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Conv));
            }, null, dueTime: 280, period: 280);
        }
    }

    private void StopThinkingTimerIfIdle()
    {
        var anyActive = false;
        foreach (var s in _thinkingStreams.Values)
            if (s.Mode.Length > 0) { anyActive = true; break; }
        if (anyActive) return;
        lock (_thinkingTimerLock)
        {
            _thinkingTimer?.Dispose();
            _thinkingTimer = null;
        }
    }

    private void ClearThinkingForKey(string key)
    {
        if (_thinkingStreams.TryRemove(key, out var stream))
        {
            stream.Mode = "";
            stream.Collapsed = false;
            stream.CollapseChars = 0;
            lock (stream.BufferLock) { stream.Buffer.Clear(); }
        }
        StopThinkingTimerIfIdle();
    }

    internal void ClearStreaming()
    {
        _streaming = false;
        _toolProgress = null;
        lock (_streamLock) { _streamBuf.Clear(); }
        _ctrlCBannerVisible = false;
        _ctrlCBannerTimer?.Dispose();
        _ctrlCBannerTimer = null;
    }

    internal void ClearThinking()
    {
        foreach (var stream in _thinkingStreams.Values)
        {
            stream.Mode = "";
            stream.Collapsed = false;
            stream.CollapseChars = 0;
            lock (stream.BufferLock) { stream.Buffer.Clear(); }
        }
        _thinkingStreams.Clear();
        lock (_thinkingTimerLock)
        {
            _thinkingTimer?.Dispose();
            _thinkingTimer = null;
        }
    }

    internal void WriteMarkdown(string md)
    {
        AddMessage(new Msg("assistant", md));
        Paint();
    }

    internal void WriteToolStart(string n, string a)
    {
        AddMessage(new Msg("tool", $"  ⧫ {n} {(a.Length > 60 ? a[..57] + "..." : a)}"));
        PaintConvThrottled(force: false);
    }

    internal void WriteToolSuccess(string n)
    {
        AddMessage(new Msg("tool", $"  ✓ {n}") { Ok = true });
        PaintConvThrottled(force: true);
    }

    internal void WriteToolError(string n, string e)
    {
        AddMessage(new Msg("tool", $"  ✗ {n}: {e}") { Err = true });
        PaintConvThrottled(force: true);
    }

    internal void WriteToolDenied(string n, string r)
    {
        AddMessage(new Msg("tool", $"  ⊘ {n}: {r}") { Err = true });
        PaintConvThrottled(force: true);
    }

    internal void WriteToolDiff(string diff)
    {
        AddMessage(new Msg("diff", diff));
        PaintConvThrottled(force: true);
    }

    internal void WriteWarning(string m)
    {
        AddMessage(new Msg("sys", $"⚠ {m}"));
        PaintConvThrottled(force: true);
    }

    internal void WriteError(string m)
    {
        AddMessage(new Msg("sys", $"✗ {m}") { Err = true });
        PaintConvThrottled(force: true);
    }

    internal void WriteInfo(string m)
    {
        AddMessage(new Msg("sys", m));
        PaintConvThrottled(force: true);
    }

    internal void WriteWelcome() => Paint();

    internal void WriteToolContent(string toolName, string filePath, string content)
    {
        var filename = Path.GetFileName(filePath);
        var allLines = content.Split('\n');

        var numbered = new List<(int Num, string Text)>();
        foreach (var line in allLines)
        {
            var tab = line.IndexOf('\t');
            if (tab > 0 && int.TryParse(line[..tab], out var num))
                numbered.Add((num, line[(tab + 1)..]));
        }
        if (numbered.Count == 0)
        {
            for (var i = 0; i < allLines.Length; i++)
                numbered.Add((i + 1, allLines[i]));
        }

        const int MaxLines = 30;
        var sb = new StringBuilder();
        sb.Append($"{B}{Fw}▶ {toolName}:{R} {Fk}{filename}{R}");
        for (var i = 0; i < Math.Min(numbered.Count, MaxLines); i++)
        {
            var (num, text) = numbered[i];
            sb.Append($"\n  {Fk}{num,4}{R}  {Fw}{text}{R}");
        }
        if (numbered.Count > MaxLines)
            sb.Append($"\n  {DM}{Fk}… ({numbered.Count - MaxLines} more lines){R}");

        AddMessage(new Msg("content", sb.ToString()));
        PaintConvThrottled(force: true);
    }

    internal void WriteTodos(IReadOnlyList<Session.TodoItem> todos)
    {
        if (todos.Count == 0) return;
        var lines = new List<string> { $"{B}{Fw}Tasks:{R}" };
        foreach (var todo in todos)
        {
            var (icon, color) = todo.Status switch
            {
                "completed"   => ("✓", Fg),
                "in_progress" => ("►", Fy),
                _             => ("○", Fk),
            };
            var text = todo.Status == "in_progress" && todo.ActiveForm is not null
                ? todo.ActiveForm
                : todo.Content;
            lines.Add($"  {color}{icon}{R} {Fw}{text}{R}");
        }
        AddMessage(new Msg("sys", string.Join('\n', lines)));
        PaintConvThrottled(force: true);
    }

    internal void AddUserMessage(string text)
    {
        AddMessage(new Msg("user", text));
        Paint();
    }

    internal void ClearConversation()
    {
        lock (_messagesLock) { _messages.Clear(); _lastUserText = ""; }
        lock (_streamLock) { _streamBuf.Clear(); }
        ClearThinking();
        _lastTokSec = 0;
        Array.Clear(_tokSecHistory);
        _tokSecHistoryIdx = 0;
        Volatile.Write(ref _windowMax, 0);
        Volatile.Write(ref _windowAvg, 0);
        InvalidateCache();
        Paint();
    }

    internal void BeginTurnPaint() => InvalidateFrameBuffer();

    private void PaintConvDirect()
    {
        if (_paintActive)
        {
            _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Conv));
            return;
        }
        lock (_writeLock) { DoPaintConvDirect(); }
    }

    private void PaintLoop()
    {
        var reader = _paintChannel.Reader;
        var ct = _paintCts?.Token ?? CancellationToken.None;
        var consecutiveErrors = 0;

        try
        {
            while (_paintActive)
            {
                if (!reader.TryRead(out var req))
                {
                    try
                    {
                        var waitTask = reader.WaitToReadAsync(ct).AsTask();
                        waitTask.Wait(250);
                    }
                    catch (OperationCanceledException) { }
                    catch (AggregateException) { }
                    continue;
                }
                if (!_paintActive) break;

                var needConv    = req.Kind == PaintKind.Conv;
                var needFull    = req.Kind == PaintKind.Full;
                var needSidebar = req.Kind == PaintKind.Sidebar;
                string? inputText  = req.Kind == PaintKind.Input ? req.InputText : null;
                var inputCursor = req.Kind == PaintKind.Input ? req.Cursor : 0;

                while (reader.TryRead(out var next))
                {
                    switch (next.Kind)
                    {
                        case PaintKind.Full:    needFull    = true; break;
                        case PaintKind.Conv:    needConv    = true; break;
                        case PaintKind.Sidebar: needSidebar = true; break;
                        case PaintKind.Input:   inputText   = next.InputText; inputCursor = next.Cursor; break;
                    }
                }

                try
                {
                    lock (_writeLock)
                    {
                        if (needFull)
                        {
                            DoPaintFull();
                        }
                        else
                        {
                            if (needConv)
                                DoPaintConvDirect();
                            else if (needSidebar)
                                DoPaintSidebar();

                            if (inputText is not null)
                                DoDrawInputText(inputText, inputCursor);
                        }
                    }
                    consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    Log.Error($"Paint exception #{consecutiveErrors} ({ex.GetType().Name}): {ex.Message}", ex);
                    if (consecutiveErrors >= PaintFailureCircuitBreaker)
                    {
                        Log.Error($"Paint thread disabled after {PaintFailureCircuitBreaker} consecutive failures. UI will stop updating until next turn.");
                        _paintActive = false;
                        break;
                    }
                    try { Thread.Sleep(50); } catch { }
                }
            }
        }
        finally
        {
            _paintActive = false;
        }
    }

    private void DoPaintFull()
    {
        try
        {
            InvalidateFrameBuffer();
            Sz();
            _paintInProgress = true;
            var sb = new StringBuilder(8192);
            sb.Append($"{E}[?25l{E}[H");

            var mainW       = Math.Max(1, _tw - _sideW);
            var currentText = _getBgInput();
            var inputH      = InputContentRows(currentText, mainW) + 2;
            var convH       = Math.Max(0, _th - inputH - 2);

            PaintConvArea(sb, mainW, convH);
            PaintUpdateBanner(sb, convH);
            PaintInputBox(sb, mainW, convH);
            PaintTabBar(sb, mainW, convH + inputH);
            PaintSidebar(sb, mainW, convH + inputH + 1);
            PaintStatusBar(sb);
            if (_ctrlCBannerVisible) PaintCtrlCBanner(sb);
            if (_contextWarningPct > 0) PaintContextWarning(sb);

            sb.Append(R);
            AppendLaneOverlay(sb);
            AppendAtOverlay(sb);
            AppendSuggestionOverlay(sb);
            AppendInputCursor(sb, mainW);
            W(sb.ToString());
            Flush();
        }
        catch (Exception ex)
        {
            Log.Error("DoPaintFull failed", ex);
        }
        finally
        {
            _paintInProgress = false;
        }
    }

    private void DoPaintConvDirect()
    {
        Sz();
        _paintInProgress = true;
        var sb = new StringBuilder(4096);
        sb.Append($"{E}[?25l");
        var mainW       = _tw - _sideW;
        var currentText = _getBgInput();
        var convH       = _th - InputContentRows(currentText, mainW) - 4;
        PaintConvArea(sb, mainW, convH);
        PaintUpdateBanner(sb, convH);
        PaintInputBox(sb, mainW, convH);
        PaintTabBar(sb, mainW, convH + InputContentRows(currentText, mainW) + 2);
        PaintSidebar(sb, mainW, _th - 1);
        PaintStatusBar(sb);
        if (_ctrlCBannerVisible) PaintCtrlCBanner(sb);
        if (_contextWarningPct > 0) PaintContextWarning(sb);
        sb.Append(R);
        AppendLaneOverlay(sb);
        AppendAtOverlay(sb);
        AppendSuggestionOverlay(sb);
        AppendInputCursor(sb, mainW);
        W(sb.ToString());
        Flush();
        _paintInProgress = false;
    }

    private void DoPaintSidebar()
    {
        try
        {
            Sz();
            var sb    = new StringBuilder(1024);
            var mainW = Math.Max(1, _tw - _sideW);
            PaintSidebar(sb, mainW, Math.Max(1, _th - 1));
            PaintStatusBar(sb);
            sb.Append(R);
            W(sb.ToString());
            Flush();
        }
        catch (Exception ex)
        {
            Log.Error("DoPaintSidebar failed", ex);
        }
    }

    private void DoDrawInputText(string text, int cursor)
    {
        try
        {
            _inputCursor = cursor;
            Sz();
            var mainW       = Math.Max(1, _tw - _sideW);
            var wrapW       = InputWrapWidth(mainW);
            var wrapped     = WrapInput(text, wrapW);
            var contentRows = Math.Clamp(wrapped.Length, 1, 5);
            var isCollapsedFully    = wrapped.Length == 1 && wrapped[0].EndsWith("Copied]");
            var isCollapsedWithTail = wrapped.Length > 1 && wrapped[0].EndsWith("Copied]");

            var firstContentRow = Math.Max(1, _th - contentRows - 2);

            int cursorRow, cursorCol;
            if (isCollapsedFully)
            {
                cursorRow = 0;
                cursorCol = wrapped[0].Length;
            }
            else if (isCollapsedWithTail)
            {
                var lastNl = text.LastIndexOf('\n');
                if (cursor > lastNl)
                {
                    var tailCursor = cursor - lastNl - 1;
                    cursorRow = 1 + (tailCursor / wrapW);
                    cursorCol = tailCursor % wrapW;
                    if (cursorRow >= contentRows)
                    {
                        cursorRow = contentRows - 1;
                        cursorCol = wrapped[cursorRow].Length;
                    }
                }
                else
                {
                    cursorRow = 0;
                    cursorCol = wrapped[0].Length;
                }
            }
            else
            {
                (cursorRow, cursorCol) = ComputeInputCursorPos(text, cursor, wrapW);
                cursorRow = Math.Min(cursorRow, contentRows - 1);
            }

            var sb = new StringBuilder((wrapW + 20) * contentRows + 256);

            var prevRows = _prevInputContentRows;
            _prevInputContentRows = contentRows;
            if (prevRows > 0 && contentRows < prevRows)
            {
                var oldFirstContent = Math.Max(1, _th - prevRows - 2);
                var divider = $"{BgInput}{Fk}{new string('─', mainW)}{R}";
                for (var row = oldFirstContent - 1; row < firstContentRow - 1; row++)
                    sb.Append($"{E}[{Math.Max(1, row + 1)};1H{BgMain}{new string(' ', mainW)}{R}");
                sb.Append($"{E}[{Math.Max(1, firstContentRow - 1)};1H{divider}");

                _prevConvFrame = null;
                if (_paintActive) _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Full));
            }
            else if (prevRows > 0 && contentRows > prevRows)
            {
                var divider = $"{BgInput}{Fk}{new string('─', mainW)}{R}";
                sb.Append($"{E}[{Math.Max(1, firstContentRow - 1)};1H{divider}");
            }

            for (var r = 0; r < contentRows; r++)
            {
                var absRow   = Math.Max(1, firstContentRow + r);
                var lineText = r < wrapped.Length ? wrapped[r] : "";
                var isIndicatorLine = lineText.EndsWith("Copied]");
                sb.Append($"{E}[{absRow};3H{BgInput}");
                sb.Append(isIndicatorLine ? $"{Fk}{IT}{lineText}" : $"{Fw}{lineText}");
                sb.Append(new string(' ', Math.Max(0, wrapW - lineText.Length)));
                sb.Append(R);
            }

            sb.Append($"{E}[{Math.Max(1, firstContentRow + cursorRow)};{3 + cursorCol}H{E}[?25h");
            W(sb.ToString());
            Flush();
        }
        catch (Exception ex)
        {
            Log.Error("DoDrawInputText failed", ex);
        }
    }

    private void AppendInputCursor(StringBuilder sb, int mainW)
    {
        var text        = _getBgInput();
        var wrapW       = InputWrapWidth(mainW);
        var wrapped     = WrapInput(text, wrapW);
        var contentRows = Math.Clamp(wrapped.Length, 1, 5);
        var firstContentRow = Math.Max(1, _th - contentRows - 2);
        var cursor      = _inputCursor;

        int cursorRow, cursorCol;
        if (wrapped.Length == 1 && wrapped[0].EndsWith("Copied]"))
        {
            cursorRow = 0;
            cursorCol = wrapped[0].Length;
        }
        else if (wrapped.Length > 1 && wrapped[0].EndsWith("Copied]"))
        {
            var lastNl = text.LastIndexOf('\n');
            if (cursor > lastNl)
            {
                var tailCursor = cursor - lastNl - 1;
                cursorRow = 1 + (tailCursor / wrapW);
                cursorCol = tailCursor % wrapW;
                if (cursorRow >= contentRows)
                {
                    cursorRow = contentRows - 1;
                    cursorCol = wrapped[cursorRow].Length;
                }
            }
            else { cursorRow = 0; cursorCol = wrapped[0].Length; }
        }
        else
        {
            (cursorRow, cursorCol) = ComputeInputCursorPos(text, cursor, wrapW);
            cursorRow = Math.Min(cursorRow, contentRows - 1);
        }

        sb.Append($"{E}[{Math.Max(1, firstContentRow + cursorRow)};{3 + cursorCol}H{E}[?25h");
    }

    private static (int row, int col) ComputeInputCursorPos(string text, int cursor, int wrapW)
    {
        var row = 0;
        var col = 0;
        for (var i = 0; i < cursor && i < text.Length; i++)
        {
            if (text[i] == '\n') { row++; col = 0; }
            else { col++; if (col >= wrapW) { row++; col = 0; } }
        }
        return (row, col);
    }

    private void PaintConvArea(StringBuilder sb, int w, int h)
    {
        var lineW = w - 4;
        if (lineW != _cachedWidth)
        {
            _cachedLines.Clear();
            _cachedMsgCount = 0;
            _cachedWidth = lineW;
        }

        var newMsgs = CopyMessagesFrom(_cachedMsgCount, out var totalCount);
        if (newMsgs.Length > 0)
        {
            for (var i = 0; i < newMsgs.Length; i++)
                RenderMsg(newMsgs[i], _cachedLines, lineW);
            _cachedMsgCount = totalCount;
        }

        if (_cachedLines.Count > TrimThreshold)
        {
            var trimCount = _cachedLines.Count - MaxCachedLines;
            _cachedLines.RemoveRange(0, trimCount);
            _trimmedLineCount += trimCount;
        }

        var lines      = _cachedLines;
        var extraStart = lines.Count;

        string? streamSnapshot  = null;
        var streamTruncated = false;
        if (_streaming)
            lock (_streamLock)
            {
                var len = _streamBuf.Length;
                if (len > 0)
                {
                    var wrapW   = w - 6;
                    var maxChars = h * (wrapW + 1);
                    if (len > maxChars)
                    {
                        var tail = len - maxChars;
                        for (var i = tail; i < tail + wrapW && i < len; i++)
                        {
                            if (_streamBuf[i] == '\n') { tail = i + 1; break; }
                        }
                        streamSnapshot  = _streamBuf.ToString(tail, len - tail);
                        streamTruncated = true;
                    }
                    else
                    {
                        streamSnapshot = _streamBuf.ToString();
                    }
                }
            }

        if (streamSnapshot is not null)
        {
            lines.Add("");
            if (streamTruncated)
                lines.Add($"  {Fk}...{R}");
            var wrapped = Wrap(streamSnapshot, w - 6);
            var skip = Math.Max(0, wrapped.Count - h);
            for (var i = skip; i < wrapped.Count; i++)
                lines.Add($"  {wrapped[i]}");
        }
        else
        {
            var snapshotKeys = _thinkingStreams.Keys.OrderBy(k => k == MainAgentKey ? "" : k, StringComparer.Ordinal).ToList();
            var firstBlock = true;
            foreach (var key in snapshotKeys)
            {
                if (!_thinkingStreams.TryGetValue(key, out var stream)) continue;
                var who = key == MainAgentKey ? null : key;
                var prefix = who is null ? "" : $"[{who}] ";
                if (stream.Collapsed)
                {
                    var approxTok = stream.CollapseChars / 4;
                    var tok = approxTok > 0 ? $" [{approxTok} tok]" : "";
                    if (firstBlock) { lines.Add(""); firstBlock = false; }
                    lines.Add($"  {Fk}◈ {prefix}Thinking{tok}{R}");
                    continue;
                }
                if (stream.Mode.Length == 0) continue;
                var frame   = System.Threading.Volatile.Read(ref stream.Frame);
                var spinner = SpinnerFrames[frame % SpinnerFrames.Length];
                var dots    = DotsFrames[frame % DotsFrames.Length];
                var label   = stream.Mode == "Waiting" ? stream.WaitingLabel : "Thinking";
                if (firstBlock) { lines.Add(""); firstBlock = false; }
                lines.Add($"  {Fbb}{spinner} {IT}{Fk}{prefix}{label}{dots}");
                string snapshot;
                lock (stream.BufferLock) { snapshot = stream.Buffer.ToString(); }
                if (snapshot.Length > 0)
                {
                    var perAgentLines = snapshotKeys.Count > 1 ? 1 : 3;
                    var thinkLines = snapshot.Split('\n').Where(l => l.Length > 0).TakeLast(perAgentLines).ToArray();
                    foreach (var ln in thinkLines)
                    {
                        var display = ln.Length > w - 6 ? ln[..(w - 6)] + "…" : ln;
                        lines.Add($"  {IT}{Fk}{display}");
                    }
                }
            }
        }

        var toolProg = _toolProgress;
        if (toolProg is not null)
        {
            var spinner = SpinnerFrames[Math.Abs(_toolProgressFrame) % SpinnerFrames.Length];
            lines.Add("");
            lines.Add($"  {Fbb}{spinner} {IT}{Fk}{toolProg}…{R}");
        }

        lock (_queueLock)
        {
            if (_messageQueue.Count > 0)
            {
                lines.Add("");
                var i = 0;
                foreach (var msg in _messageQueue)
                {
                    i++;
                    var preview = msg.Length > w - 14 ? msg[..(w - 17)] + "…" : msg;
                    lines.Add($"  {Fy}⏳ Queued {i}:{R} {Fk}{preview}{R}");
                }
            }
        }

        if (_autoScroll) _scrollOffset = 0;

        var maxOffset = Math.Max(0, lines.Count - h);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);
        var start = Math.Max(0, lines.Count - h - _scrollOffset);

        var safeH = Math.Max(1, h);
        if (_prevConvFrame is null || _prevConvFrame.Length < safeH)
            _prevConvFrame = new string[safeH];
        if (_rowPlainText.Length < safeH)
            _rowPlainText = new string[safeH];

        var selActive = _selAnchorRow >= 0;
        var (ssr, ssc, ser, sec) = selActive ? NormalizedSelection() : (-1, 0, -1, 0);

        for (var row = 0; row < h; row++)
        {
            var idx = start + row;
            string newContent;
            var safeW = Math.Max(0, w);
            var plain = idx < lines.Count ? AnsiRe.Replace(lines[idx], "") : "";
            if (row < _rowPlainText.Length) _rowPlainText[row] = plain;

            if (selActive && row >= ssr && row <= ser)
            {
                var from = row == ssr ? Math.Clamp(ssc, 0, plain.Length) : 0;
                var to   = row == ser ? Math.Clamp(sec, 0, plain.Length) : plain.Length;
                if (to < from) (from, to) = (to, from);
                var content = $"{plain[..from]}{E}[7m{plain[from..to]}{E}[27m{plain[to..]}";
                newContent = $"{BgMain} {PadR(content, Math.Max(0, safeW - 1))}{R}";
            }
            else if (idx < lines.Count)
                newContent = $"{BgMain} {PadR(lines[idx], Math.Max(0, safeW - 1))}{R}";
            else
                newContent = $"{BgMain}{new string(' ', safeW)}{R}";

            if (row < _prevConvFrame.Length && _prevConvFrame[row] != newContent)
            {
                sb.Append($"{E}[{row + 1};1H");
                sb.Append(newContent);
                _prevConvFrame[row] = newContent;
            }
        }

        if (_prevFrameHeight > h)
        {
            var blankLine = $"{BgMain}{new string(' ', Math.Max(0, w))}{R}";
            for (var row = h; row < _prevFrameHeight && row < _prevConvFrame.Length; row++)
            {
                if (_prevConvFrame[row] != blankLine)
                {
                    sb.Append($"{E}[{row + 1};1H");
                    sb.Append(blankLine);
                    _prevConvFrame[row] = blankLine;
                }
            }
        }

        _prevFrameWidth  = w;
        _prevFrameHeight = h;

        if (lines.Count > extraStart)
            lines.RemoveRange(extraStart, lines.Count - extraStart);
    }

    private void PaintInputBox(StringBuilder sb, int w, int startRow)
    {
        var currentText = _getBgInput();
        var wrapW       = InputWrapWidth(w);
        var wrapped     = WrapInput(currentText, wrapW);
        var contentRows = Math.Clamp(wrapped.Length, 1, 5);
        var divider     = $"{BgInput}{Fk}{new string('─', w)}{R}";

        _prevInputContentRows = contentRows;

        sb.Append($"{E}[{startRow + 1};1H{divider}");

        for (var r = 0; r < contentRows; r++)
        {
            sb.Append($"{E}[{startRow + r + 2};1H");
            var lineText = wrapped.Length > r ? wrapped[r] : "";
            var isIndicatorLine = lineText.EndsWith("Copied]");
            sb.Append($"{BgInput}{Fbb}│{R}{BgInput} ");
            sb.Append(isIndicatorLine ? $"{Fk}{IT}{lineText}" : $"{Fw}{lineText}");
            sb.Append(new string(' ', Math.Max(0, wrapW - lineText.Length)));
            sb.Append(R);
        }

        sb.Append($"{E}[{startRow + contentRows + 2};1H{divider}");
    }

    private void PaintTabBar(StringBuilder sb, int w, int row)
    {
        sb.Append($"{E}[{row + 1};1H{BgStatus}");
        var model = config.Llm.Model;
        if (model.Length > 50) model = model[..49] + "…";
        sb.Append($" {Fbb}{B}Build{R}{BgStatus}  {B}{Fw}{model}{R}{BgStatus}");
        sb.Append(new string(' ', Math.Max(0, w - 8 - model.Length)));
        sb.Append(R);
    }

    private void PaintSidebar(StringBuilder sb, int mainW, int totalH)
    {
        if (_sideW == 0) return;
        var col   = mainW + 1;
        var lines = new List<string>(20);

        var lastUserText = GetLastUserText();
        lines.Add($"{B}{Fw}{(!string.IsNullOrEmpty(lastUserText) ? Trunc(lastUserText, _sideW - 2) : "New session")}{R}");
        lines.Add("");

        var tracker    = session.Meta.TokenTracker;
        var tok        = tracker?.TotalTokens ?? 0;
        var lastPrompt = tracker?.LastPromptTokens ?? 0;
        var ctx        = config.Llm.ContextSize;
        var sessionPct = ctx > 0 ? (int)((double)tok / ctx * 100) : 0;
        var promptPct  = ctx > 0 ? (int)((double)lastPrompt / ctx * 100) : 0;
        var promptColor = promptPct >= 95 ? Fr : promptPct >= 80 ? Fy : Fk;
        lines.Add($"{B}Context{R}");
        lines.Add($"{Fk}{tok:N0} tokens · {sessionPct}% session{R}");
        if (lastPrompt > 0)
            lines.Add($"{promptColor}{lastPrompt:N0} · {promptPct}% last turn{R}");
        lines.Add("");

        lines.Add($"{B}Tokens/sec{R}");
        if (_lastTokSec > 0 || HasHistory())
        {
            lines.Add($"{Fc}{_lastTokSec:F1} tok/s{R}");
            lines.Add(BuildSparkline(_sideW - 2));
            var wMax = Volatile.Read(ref _windowMax);
            var wAvg = Volatile.Read(ref _windowAvg);
            if (wMax > 0)
            {
                lines.Add($" {Fk}MAX:{R} {Fw}{wMax:F1}{Fk} t/s{R}");
                lines.Add($" {Fk}AVG:{R} {Fw}{wAvg:F1}{Fk} t/s{R}");
            }
        }
        else
        {
            lines.Add($"{Fk}—{R}");
            lines.Add($"{Fk}{new string('▁', Math.Max(0, Math.Min(_sideW - 2, 20)))}{R}");
        }
        lines.Add("");

        lines.Add($"{B}LSP{R}");
        lines.Add($"{Fk}LSPs activate as files are read{R}");
        lines.Add("");
        lines.Add($"{Fk}{TruncPath(config.WorkingDirectory, _sideW - 2)}{R}");
        lines.Add("");
        lines.Add($"{Fg}● {B}OpenMono{R} {Fk}local{R}");

        var sideH = _th - 1;
        if (_prevSideFrame is null || _prevSideFrame.Length < sideH)
            _prevSideFrame = new string[sideH];

        for (var row = 0; row < sideH; row++)
        {
            string newContent;
            if (row < lines.Count)
                newContent = $"{BgSide}{PadR(lines[row], _sideW)}{R}";
            else
                newContent = $"{BgSide}{new string(' ', _sideW)}{R}";

            if (_prevSideFrame[row] != newContent)
            {
                sb.Append($"{E}[{row + 1};{col}H");
                sb.Append(newContent);
                _prevSideFrame[row] = newContent;
            }
        }
    }

    private void PaintStatusBar(StringBuilder sb)
    {
        if (_toast is not null && DateTime.UtcNow < _toastExpiry)
        {
            var toast = $" {B}{Fbb}✓ {_toast}{R}{BgStatus}";
            sb.Append($"{E}[?7l{E}[{_th};1H{E}[2K{BgStatus}");
            sb.Append(toast);
            sb.Append(new string(' ', Math.Max(0, _tw - VisLen(toast))));
            sb.Append($"{R}{E}[?7h");
            return;
        }

        sb.Append($"{E}[?7l{E}[{_th};1H{E}[2K{BgStatus}");
        var tracker = session.Meta.TokenTracker;
        var tok     = tracker?.LastPromptTokens ?? 0;
        var ctx     = config.Llm.ContextSize;
        var pct     = ctx > 0 ? (int)((double)tok / ctx * 100) : 0;

        var now = Stopwatch.GetTimestamp();
        if (now - _lastHeartbeatTick > HeartbeatIntervalTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
        {
            _heartbeatFrame = (_heartbeatFrame + 1) % HeartbeatFrames.Length;
            _lastHeartbeatTick = now;
        }
        var isCompacting = session.Meta.IsCompacting;
        // Distinct glyph + color while compacting so the ring reads as "actively recalculating",
        // not just the normal idle pulse — the (pct%) next to it is stale until compaction finishes.
        var pulse = isCompacting
            ? SpinnerFrames[_heartbeatFrame % SpinnerFrames.Length]
            : HeartbeatFrames[_heartbeatFrame];
        var pulseColor = isCompacting ? Fy : (_streaming && _lastTokSec > 0 ? Fc : Fg);
        var compactingTag = isCompacting ? $"{Fy} compacting…{R}{BgStatus}" : "";

        var tokStr = FmtTok(tok);
        var wMax   = Volatile.Read(ref _windowMax);
        var wAvg   = Volatile.Read(ref _windowAvg);
        var maxAvgStr = wMax > 0
            ? $"  {Fk}MAX{R}{BgStatus} {Fw}{wMax:F1}{R}{BgStatus}{Fk} t/s{R}{BgStatus}  {Fk}AVG{R}{BgStatus} {Fw}{wAvg:F1}{R}{BgStatus}{Fk} t/s{R}{BgStatus}"
            : "";

        string left;
        if (_streaming && _lastTokSec > 0)
            left = $" {pulseColor}{pulse}{R}{BgStatus} {tokStr} ({pct}%){compactingTag}{maxAvgStr}  {Fg}●{R}{BgStatus} {Fw}{_lastTokSec:F1} tok/s{R}{BgStatus}";
        else
            left = $" {pulseColor}{pulse}{R}{BgStatus} {tokStr} ({pct}%){compactingTag}{maxAvgStr}";

        sb.Append(left);
        var visL = VisLen(left);

        var scrollIndicator = _scrollOffset > 0
            ? $"{Fy}↑ PgUp/PgDn to scroll{R}{BgStatus}  "
            : "";
        var canCancel = _isTurnActive() || QueuedCount > 0;
        var cancelHint = canCancel ? $"{Fk}esc{R}{BgStatus} {Fw}cancel{R}{BgStatus}" : "";
        var modeIndicator = session.Meta.PlanMode
            ? $"{Fk}[{R}{Fy}PLAN{R}{Fk}]{R}{BgStatus} "
            : $"{Fk}[{R}{Fg}BUILD{R}{Fk}]{R}{BgStatus} ";
        var mid   = $"{modeIndicator}{scrollIndicator}{cancelHint}";
        var right = $"{Fk}ctrl+c{R}{BgStatus} {Fw}quit{R}{BgStatus}   {Fk}ctrl+p{R}{BgStatus} {Fw}commands{R}{BgStatus} ";
        var visM  = VisLen(mid);
        var visR  = VisLen(right);
        var g1    = Math.Max(0, (_tw - visL - visM - visR) / 2);
        var g2    = Math.Max(0, _tw - visL - g1 - visM - visR);
        sb.Append(new string(' ', g1));
        sb.Append(mid);
        sb.Append(new string(' ', g2));
        sb.Append(right);
        sb.Append($"{R}{E}[?7h");
    }

    private void PaintCtrlCBanner(StringBuilder sb)
    {
        var w      = _tw > 0 ? _tw : 80;
        var mainW  = Math.Max(1, _tw - _sideW);
        var inputH = InputContentRows(_getBgInput(), mainW) + 2;
        var row    = Math.Max(1, _th - inputH - 2);
        var msg    = "  ^C  Press Ctrl+C one more time to exit";
        var padded = msg.Length < w ? msg + new string(' ', w - msg.Length) : msg[..w];
        sb.Append($"{E}[{row};1H\x1b[43;30m{padded}{R}");
    }

    // Renders the "update available" banner on the line directly above the input
    // box (like Claude Code's update notice). No-op when no update is available.
    private void PaintUpdateBanner(StringBuilder sb, int convH)
    {
        if (string.IsNullOrEmpty(_updateNotice)) return;
        var w      = _tw > 0 ? _tw : 80;
        var msg    = $"  ⬆  {_updateNotice}";
        var padded = msg.Length < w ? msg + new string(' ', w - msg.Length) : msg[..w];
        var row    = Math.Max(1, convH);   // the line just above the input box
        sb.Append($"{E}[{row};1H\x1b[42;30m{padded}{R}");
    }

    private void PaintContextWarning(StringBuilder sb)
    {
        var pct = _contextWarningPct;
        if (pct <= 0) return;
        var w   = _tw > 0 ? _tw : 80;
        var msg = $"  ⚠  Context {pct}% full — type /compact to summarize and free space";
        var padded = msg.Length < w ? msg + new string(' ', w - msg.Length) : msg[..w];
        var row    = 1;
        var color  = pct >= 95 ? "\x1b[41;97m" : "\x1b[43;30m";
        sb.Append($"{E}[{row};1H{color}{padded}{R}");
    }

    private Msg[] CopyMessagesFrom(int startIdx, out int totalCount)
    {
        lock (_messagesLock)
        {
            totalCount = _messages.Count;
            if (startIdx >= totalCount) return [];
            var arr = new Msg[totalCount - startIdx];
            _messages.CopyTo(startIdx, arr, 0, arr.Length);
            return arr;
        }
    }

    private string GetLastUserText()
    {
        lock (_messagesLock) return _lastUserText;
    }

    private bool HasHistory()
    {
        for (var i = 0; i < 30; i++) if (_tokSecHistory[i] > 0) return true;
        return false;
    }

    private string BuildSparkline(int width)
    {
        var n = Math.Max(0, Math.Min(width, 30));
        var samples = new double[n];
        var s = _tokSecHistoryIdx >= n ? _tokSecHistoryIdx - n : 0;
        for (var i = 0; i < n; i++) samples[i] = _tokSecHistory[(s + i) % 30];

        var max = 0.0;
        foreach (var v in samples) if (v > max) max = v;
        if (max < 1) max = 1;

        ReadOnlySpan<char> blocks = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
        var spark = new char[n];
        for (var i = 0; i < n; i++)
            spark[i] = blocks[Math.Clamp((int)(samples[i] / max * 7), 0, 7)];
        return $"{Fg}{new string(spark)}{R}";
    }

    private void ComputeWindowStats()
    {
        var max   = 0.0;
        var sum   = 0.0;
        var count = 0;
        var n     = Math.Min(_tokSecHistoryIdx, 30);
        var start = _tokSecHistoryIdx >= 30 ? _tokSecHistoryIdx - 30 : 0;
        for (var i = 0; i < n; i++)
        {
            var v = _tokSecHistory[(start + i) % 30];
            if (v > 0)
            {
                if (v > max) max = v;
                sum += v;
                count++;
            }
        }
        Volatile.Write(ref _windowMax, max);
        Volatile.Write(ref _windowAvg, count > 0 ? Math.Round(sum / count, 1) : 0);
    }

    private static string FmtTok(int t) => t >= 1000 ? $"{t / 1000.0:F1}K" : t.ToString();

    private static string TruncPath(string p, int n)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p.StartsWith(home)) p = "~" + p[home.Length..];
        return p.Length <= n ? p : "…" + p[^(n - 1)..];
    }

    private void W(string s) => terminal.WriteAsync(s).GetAwaiter().GetResult();

    private static void RenderMsg(Msg m, List<string> lines, int w)
    {
        switch (m.Role)
        {
            case "user":
                lines.Add("");
                foreach (var l in Wrap(m.Text, w - 5))
                    lines.Add($"  {Fy}{B}┃{R}{BgInput} {B}{Fw}{l}{R}{BgInput}{Pad(Math.Max(0, w - 5 - VisLen(l)))}{R}");
                break;
            case "assistant":
            {
                lines.Add("");
                var mdLines = AnsiMarkdown.Render(m.Text, w - 4);
                foreach (var l in mdLines)
                    lines.Add($"  {Fk}│{R} {l}");
                if (m.Footer is not null)
                    lines.Add($"  {Fbb}■{R}  {Fk}{m.Footer}{R}");
                break;
            }
            case "tool":
            {
                var icon   = m.Ok ? $"{Fg}✓" : m.Err ? $"{Fr}✗" : $"{Fk}⧫";
                var border = m.Ok ? Fg : m.Err ? Fr : Fk;

                var text = m.Text.TrimStart();
                if (text.StartsWith("⧫") || text.StartsWith("✓") || text.StartsWith("✗") || text.StartsWith("⊘"))
                    text = text.Length > 2 ? text[2..] : text;

                var truncated = text.Length > w - 8
                    ? text[..(w - 11)] + "..."
                    : text;

                lines.Add($"  {border}┌ {icon} {R}{DM}{truncated}{R}");
                if (m.Footer is not null)
                    lines.Add($"  {border}└ {Fk}{m.Footer}{R}");
                break;
            }
            case "sys":
            {
                var wrapped     = Wrap(m.Text, w - 4);
                const int maxSysLines = 10;
                var style = m.Err ? Fr : Fk;

                if (wrapped.Count > maxSysLines)
                {
                    for (var i = 0; i < maxSysLines - 1; i++)
                        lines.Add($"  {IT}{style}{wrapped[i]}{R}");
                    lines.Add($"  {DM}{Fk}... ({wrapped.Count - maxSysLines + 1} more){R}");
                }
                else
                {
                    foreach (var l in wrapped)
                        lines.Add($"  {IT}{style}{l}{R}");
                }
                break;
            }
            case "content":
            {
                var contentLines = m.Text.Split('\n');
                const int maxContentLines = 33;
                for (var i = 0; i < Math.Min(contentLines.Length, maxContentLines); i++)
                    lines.Add($"  {Fk}{contentLines[i]}{R}");
                if (contentLines.Length > maxContentLines)
                    lines.Add($"  {DM}{Fk}… ({contentLines.Length - maxContentLines} more lines){R}");
                break;
            }
            case "diff":
            {
                const string BgAdd     = "\x1b[48;2;0;40;0m";
                const string FgAddNum  = "\x1b[38;2;80;200;100m";
                const string FgAddCode = "\x1b[38;2;190;255;190m";
                const string BgRem     = "\x1b[48;2;50;0;0m";
                const string FgRemNum  = "\x1b[38;2;210;80;80m";
                const string FgRemCode = "\x1b[38;2;255;175;175m";

                var diffLines = m.Text.Replace("\r\n", "\n").Split('\n');
                const int maxDiffLines = 60;
                var show = Math.Min(diffLines.Length, maxDiffLines);
                lines.Add("");
                var oldLine = 0;
                var newLine = 0;


                var fullW = w + 3;
                for (var i = 0; i < show; i++)
                {
                    var dl = diffLines[i];
                    if (dl.StartsWith("+++") || dl.StartsWith("---"))
                        continue;
                    if (dl.StartsWith("@@"))
                    {
                        var hunk = HunkHeaderRe.Match(dl);
                        if (hunk.Success)
                        {
                            oldLine = int.Parse(hunk.Groups[1].Value);
                            newLine = int.Parse(hunk.Groups[2].Value);
                        }
                        lines.Add($"  {DM}{Fk}{dl}{R}");
                        continue;
                    }
                    if (dl.StartsWith('+'))
                    {
                        var code   = dl.Length > 1 ? dl[1..] : "";
                        var header = $" {newLine,4} +  ";
                        var fill   = Math.Max(0, fullW - 9 - VisLen(code));
                        lines.Add($"{BgAdd}{FgAddNum}{header}{FgAddCode}{code}{new string(' ', fill)}{R}");
                        newLine++;
                    }
                    else if (dl.StartsWith('-'))
                    {
                        var code   = dl.Length > 1 ? dl[1..] : "";
                        var header = $" {oldLine,4} -  ";
                        var fill   = Math.Max(0, fullW - 9 - VisLen(code));
                        lines.Add($"{BgRem}{FgRemNum}{header}{FgRemCode}{code}{new string(' ', fill)}{R}");
                        oldLine++;
                    }
                    else
                    {
                        var code = dl.Length > 0 && dl[0] == ' ' ? dl[1..] : dl;
                        lines.Add($"  {Fk}{newLine,4}{R}     {Fk}{code}{R}");
                        oldLine++;
                        newLine++;
                    }
                }
                if (diffLines.Length > maxDiffLines)
                    lines.Add($"  {DM}{Fk}… {diffLines.Length - maxDiffLines} more lines{R}");
                break;
            }
        }
    }

    internal sealed class Msg(string role, string text)
    {
        public string Role    { get; } = role;
        public string Text    { get; } = text;
        public string? Footer { get; init; }
        public bool Ok  { get; init; }
        public bool Err { get; init; }
    }
}
