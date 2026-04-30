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
    internal const string BgMain   = "\x1b[40m";
    internal const string BgInput  = "\x1b[40m";
    internal const string BgStatus = "\x1b[40m";
    internal const string BgSide   = "\x1b[40m";
    internal const string BgSugg   = "\x1b[40m";

    internal const int MaxCachedLines           = 5000;
    internal const int TrimThreshold            = 6000;
    internal const long PaintIntervalTicks      = 50  * TimeSpan.TicksPerMillisecond;
    internal const long TokenPaintIntervalTicks = 500 * TimeSpan.TicksPerMillisecond;
    internal const long HeartbeatIntervalTicks  = 500 * TimeSpan.TicksPerMillisecond;
    internal const int PaintFailureCircuitBreaker = 10;

    private enum PaintKind { Conv, Full, Input, Sidebar }
    private readonly record struct PaintRequest(PaintKind Kind, string? InputText = null, int Cursor = 0);

    // ── Animated frames ──────────────────────────────────────────────────

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

    private string _thinking = "";
    private int _thinkingFrame;
    private System.Threading.Timer? _thinkingTimer;
    private readonly StringBuilder _thinkingBuf = new();
    private readonly object _thinkingBufLock = new();
    private bool _thinkingCollapsed;
    private int _thinkingCollapseChars;

    private int _heartbeatFrame;
    private long _lastHeartbeatTick;

    private volatile bool _ctrlCBannerVisible;
    private System.Threading.Timer? _ctrlCBannerTimer;

    private volatile int _contextWarningPct;

    private string[]? _prevConvFrame;
    private string[]? _prevSideFrame;
    private int _prevFrameWidth;
    private int _prevFrameHeight;

    private readonly Channel<PaintRequest> _paintChannel = Channel.CreateUnbounded<PaintRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private Thread? _paintThread;
    private volatile bool _paintActive;
    private volatile bool _paintInProgress;
    private CancellationTokenSource? _paintCts;

    private readonly List<string> _cachedLines = [];
    private int _cachedMsgCount;
    private int _cachedWidth;
    private int _trimmedLineCount;

    private long _lastPaintTick;
    private long _lastTokenPaintTick;

    private int _scrollOffset;
    private bool _autoScroll = true;

    private Func<string> _getBgInput = () => "";

    internal void SetBgInputProvider(Func<string> getter) => _getBgInput = getter;

    internal bool Verbose { get; set; }

    internal bool PaintInProgress => _paintInProgress;
    internal bool IsStreaming => _streaming;

    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiPattern();
    private static readonly Regex AnsiRe = AnsiPattern();

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
        var lines = new List<string>();
        for (var pos = 0; pos < text.Length; pos += wrapW)
            lines.Add(text.Substring(pos, Math.Min(wrapW, text.Length - pos)));
        return [.. lines];
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

    internal void ShowCtrlCBanner()
    {
        _ctrlCBannerVisible = true;
        _ctrlCBannerTimer?.Dispose();
        _ctrlCBannerTimer = new System.Threading.Timer(_ =>
        {
            _ctrlCBannerVisible = false;
            _ctrlCBannerTimer = null;
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

        // 3-second window for instantaneous tok/s
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

    internal void EndAssistantResponse(int tokens)
    {
        _turnTimer.Stop();
        _streaming = false;
        var sec = _turnTimer.Elapsed.TotalSeconds;
        ComputeWindowStats();
        string finalText;
        lock (_streamLock) { finalText = _streamBuf.ToString(); _streamBuf.Clear(); }
        var footer = tokens > 0 ? $"{sec:F1}s · {tokens} tok" : $"{sec:F1}s";
        AddMessage(new Msg("assistant", finalText)
        {
            Footer = footer
        });
        Paint();
    }

    internal void AppendThinking(string text)
    {
        lock (_thinkingBufLock) { _thinkingBuf.Append(text); }
        if (_thinking.Length == 0)
        {
            _thinking = "Thinking";
            _thinkingFrame = 0;
            _thinkingTimer?.Dispose();
            _thinkingTimer = new System.Threading.Timer(_ =>
            {
                System.Threading.Interlocked.Increment(ref _thinkingFrame);
                if (_paintActive)
                    _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Conv));
            }, null, dueTime: 280, period: 280);
        }
        PaintConvThrottled(force: false);
    }

    internal void CollapseThinking(int charCount)
    {
        _thinkingCollapsed = true;
        _thinkingCollapseChars = charCount;
        _thinking = "";
        _thinkingTimer?.Dispose();
        _thinkingTimer = null;
        lock (_thinkingBufLock) { _thinkingBuf.Clear(); }
        PaintConvThrottled(force: true);
    }

    internal void ShowWaitingIndicator()
    {
        if (_thinking.Length == 0)
        {
            _thinking = "Waiting";
            _thinkingFrame = 0;
            _thinkingTimer?.Dispose();
            _thinkingTimer = new System.Threading.Timer(_ =>
            {
                System.Threading.Interlocked.Increment(ref _thinkingFrame);
                if (_paintActive)
                    _paintChannel.Writer.TryWrite(new PaintRequest(PaintKind.Conv));
            }, null, dueTime: 280, period: 280);
            PaintConvThrottled(force: true);
        }
    }

    internal void ClearWaitingIndicator()
    {
        if (_thinking == "Waiting")
            ClearThinking();
    }

    internal void ClearStreaming()
    {
        _streaming = false;
        lock (_streamLock) { _streamBuf.Clear(); }
        _ctrlCBannerVisible = false;
        _ctrlCBannerTimer?.Dispose();
        _ctrlCBannerTimer = null;
    }

    internal void ClearThinking()
    {
        _thinking = "";
        _thinkingCollapsed = false;
        _thinkingCollapseChars = 0;
        _thinkingTimer?.Dispose();
        _thinkingTimer = null;
        lock (_thinkingBufLock) { _thinkingBuf.Clear(); }
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

    internal void AddUserMessage(string text)
    {
        AddMessage(new Msg("user", text));
        Paint();
    }

    internal void ClearConversation()
    {
        lock (_messagesLock) { _messages.Clear(); _lastUserText = ""; }
        lock (_streamLock) { _streamBuf.Clear(); }
        _thinking = "";
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
            PaintInputBox(sb, mainW, convH);
            PaintTabBar(sb, mainW, convH + inputH);
            PaintSidebar(sb, mainW, convH + inputH + 1);
            PaintStatusBar(sb);
            if (_ctrlCBannerVisible) PaintCtrlCBanner(sb);
            if (_contextWarningPct > 0) PaintContextWarning(sb);

            sb.Append(R);
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
        PaintSidebar(sb, mainW, _th - 1);
        PaintStatusBar(sb);
        if (_ctrlCBannerVisible) PaintCtrlCBanner(sb);
        if (_contextWarningPct > 0) PaintContextWarning(sb);
        sb.Append(R);
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
            Sz();
            var mainW       = Math.Max(1, _tw - _sideW);
            var wrapW       = InputWrapWidth(mainW);
            var wrapped     = WrapInput(text, wrapW);
            var contentRows = Math.Clamp(wrapped.Length, 1, 5);

            var firstContentRow = Math.Max(1, _th - contentRows - 2);
            var cursorRow       = Math.Min(cursor / wrapW, contentRows - 1);
            var cursorCol       = cursor % wrapW;

            var sb = new StringBuilder((wrapW + 20) * contentRows + 32);
            for (var r = 0; r < contentRows; r++)
            {
                var absRow   = Math.Max(1, firstContentRow + r);
                var lineText = r < wrapped.Length ? wrapped[r] : "";
                sb.Append($"{E}[{absRow};3H{BgInput}{Fw}{lineText}");
                sb.Append(new string(' ', Math.Max(0, wrapW - lineText.Length)));
                sb.Append(R);
            }

            sb.Append($"{E}[{Math.Max(1, firstContentRow + cursorRow)};{3 + cursorCol}H");
            W(sb.ToString());
            Flush();
        }
        catch (Exception ex)
        {
            Log.Error("DoDrawInputText failed", ex);
        }
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
        else if (_thinkingCollapsed)
        {
            var approxTok = _thinkingCollapseChars / 4;
            var tok = approxTok > 0 ? $" [{approxTok} tok]" : "";
            lines.Add("");
            lines.Add($"  {Fk}◈ Thinking{tok}{R}");
        }
        else if (_thinking.Length > 0)
        {
            var frame   = System.Threading.Volatile.Read(ref _thinkingFrame);
            var spinner = SpinnerFrames[frame % SpinnerFrames.Length];
            var dots    = DotsFrames[frame % DotsFrames.Length];
            lines.Add($"  {Fbb}{spinner} {IT}{Fk}Thinking{dots}");
            string snapshot;
            lock (_thinkingBufLock) { snapshot = _thinkingBuf.ToString(); }
            if (snapshot.Length > 0)
            {
                var thinkLines = snapshot.Split('\n').Where(l => l.Length > 0).TakeLast(3).ToArray();
                foreach (var ln in thinkLines)
                {
                    var display = ln.Length > w - 6 ? ln[..(w - 6)] + "…" : ln;
                    lines.Add($"  {IT}{Fk}{display}");
                }
            }
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
        {
            _prevConvFrame     = new string[safeH];
            _prevFrameWidth    = w;
            _prevFrameHeight   = h;
        }

        for (var row = 0; row < h; row++)
        {
            var idx = start + row;
            string newContent;
            var safeW = Math.Max(0, w);
            if (idx < lines.Count)
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

        sb.Append($"{E}[{startRow + 1};1H{divider}");

        for (var r = 0; r < contentRows; r++)
        {
            sb.Append($"{E}[{startRow + r + 2};1H");
            var lineText = wrapped.Length > r ? wrapped[r] : "";
            sb.Append($"{BgInput}{Fbb}│{R}{BgInput} {Fw}{lineText}");
            sb.Append(new string(' ', Math.Max(0, wrapW - lineText.Length)));
            sb.Append(R);
        }

        sb.Append($"{E}[{startRow + contentRows + 2};1H{divider}");
    }

    private void PaintTabBar(StringBuilder sb, int w, int row)
    {
        sb.Append($"{E}[{row + 1};1H{BgStatus}");
        var model = config.Llm.Model;
        if (model.Length > 20) model = model[..20];
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
        sb.Append($"{E}[?7l{E}[{_th};1H{E}[2K{BgStatus}");
        var tracker = session.Meta.TokenTracker;
        var tok     = tracker?.TotalTokens ?? 0;
        var ctx     = config.Llm.ContextSize;
        var pct     = ctx > 0 ? (int)((double)tok / ctx * 100) : 0;

        var now = Stopwatch.GetTimestamp();
        if (now - _lastHeartbeatTick > HeartbeatIntervalTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
        {
            _heartbeatFrame = (_heartbeatFrame + 1) % HeartbeatFrames.Length;
            _lastHeartbeatTick = now;
        }
        var pulse = HeartbeatFrames[_heartbeatFrame];

        var tokStr = FmtTok(tok);
        var wMax   = Volatile.Read(ref _windowMax);
        var wAvg   = Volatile.Read(ref _windowAvg);
        var maxAvgStr = wMax > 0
            ? $"  {Fk}MAX{R}{BgStatus} {Fw}{wMax:F1}{R}{BgStatus}{Fk} t/s{R}{BgStatus}  {Fk}AVG{R}{BgStatus} {Fw}{wAvg:F1}{R}{BgStatus}{Fk} t/s{R}{BgStatus}"
            : "";

        string left;
        if (_streaming && _lastTokSec > 0)
            left = $" {Fc}{pulse}{R}{BgStatus} {tokStr} ({pct}%){maxAvgStr}  {Fg}●{R}{BgStatus} {Fw}{_lastTokSec:F1} tok/s{R}{BgStatus}";
        else
            left = $" {Fg}{pulse}{R}{BgStatus} {tokStr} ({pct}%){maxAvgStr}";

        sb.Append(left);
        var visL = VisLen(left);

        var scrollIndicator = _scrollOffset > 0
            ? $"{Fy}↑ PgUp/PgDn to scroll{R}{BgStatus}  "
            : "";
        var mid   = $"{scrollIndicator}{Fk}esc{R}{BgStatus} {Fw}cancel{R}{BgStatus}";
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
        var w   = _tw > 0 ? _tw : 80;
        var msg = "  ^C  Press Ctrl+C one more time to exit";
        var padded = msg.Length < w ? msg + new string(' ', w - msg.Length) : msg[..w];
        sb.Append($"{E}[1;1H\x1b[43;30m{padded}{R}");
    }

    private void PaintContextWarning(StringBuilder sb)
    {
        var pct = _contextWarningPct;
        if (pct <= 0) return;
        var w   = _tw > 0 ? _tw : 80;
        var msg = $"  ⚠  Context {pct}% full — type /compact to summarize and free space";
        var padded = msg.Length < w ? msg + new string(' ', w - msg.Length) : msg[..w];
        var row    = _ctrlCBannerVisible ? 2 : 1;
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
                const int maxLines  = 50;
                const int keepLines = 45;
                if (mdLines.Count > maxLines)
                {
                    for (var i = 0; i < keepLines; i++)
                        lines.Add($"  {Fk}│{R} {mdLines[i]}");
                    lines.Add($"  {Fk}│{R} {DM}{Fk}... ({mdLines.Count - keepLines} more lines){R}");
                }
                else
                {
                    foreach (var l in mdLines)
                        lines.Add($"  {Fk}│{R} {l}");
                }
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
            case "diff":
            {
                var diffLines = m.Text.Replace("\r\n", "\n").Split('\n');
                const int maxDiffLines = 40;
                var show = Math.Min(diffLines.Length, maxDiffLines);
                lines.Add("");
                for (var i = 0; i < show; i++)
                {
                    var dl = diffLines[i];
                    string styled;
                    if (dl.StartsWith("+++") || dl.StartsWith("---"))
                        styled = $"  {DM}{Fk}{dl}{R}";
                    else if (dl.StartsWith("@@"))
                        styled = $"  {Fc}{dl}{R}";
                    else if (dl.StartsWith('+'))
                        styled = $"  {Fg}{dl}{R}";
                    else if (dl.StartsWith('-'))
                        styled = $"  {Fr}{dl}{R}";
                    else
                        styled = $"  {Fk}{dl}{R}";
                    lines.Add(styled);
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
