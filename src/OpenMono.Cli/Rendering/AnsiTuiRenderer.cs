using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Session;

namespace OpenMono.Rendering;

public sealed class AnsiTuiRenderer : IRenderer
{
    private readonly AnsiPainter           _painter;
    private readonly AnsiSuggestionOverlay _overlay;
    private readonly AnsiInputReader       _inputReader;
    private readonly ITerminal             _terminal;

    private volatile bool _inFullScreen;

    public AnsiTuiRenderer(AppConfig config, SessionState session, ITerminal terminal)
    {
        _terminal    = terminal;
        _painter     = new AnsiPainter(config, session, terminal);
        _overlay     = new AnsiSuggestionOverlay(config, _painter);
        _inputReader = new AnsiInputReader(_painter, _overlay, terminal);

        _painter.SetBgInputProvider(() => _inputReader.BgInputText);
        _inputReader.OnSafeExit = SafeExit;

        terminal.InterruptRequested += _ => SafeExit();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeExit();
    }

    public void AddUserMessage(string text) => _painter.AddUserMessage(text);

    public void OnTokensUpdated() => _painter.OnTokensUpdated();

    public string? DequeueMessage() => _painter.DequeueMessage();
    public int QueuedCount => _painter.QueuedCount;

    public CancellationTokenSource? CurrentTurnCts
    {
        get => _inputReader.CurrentTurnCts;
        set => _inputReader.CurrentTurnCts = value;
    }

    public void EnterFullScreen()
    {
        _painter.Sz();
        _painter.Write($"{AnsiPainter.E}[?1049h{AnsiPainter.E}[?25l{AnsiPainter.E}[2J{AnsiPainter.E}[?1000h{AnsiPainter.E}[?1006h");
        AnsiPainter.Flush();
        _inFullScreen = true;
        _painter.InvalidateCache();
        _painter.Paint();
    }

    public void ExitFullScreen()
    {
        if (!_inFullScreen) return;
        _inFullScreen = false;
        _terminal.WriteAsync($"{AnsiPainter.E}[?1000l{AnsiPainter.E}[?1006l{AnsiPainter.E}[?25h{AnsiPainter.E}[?1049l{AnsiPainter.R}").GetAwaiter().GetResult();
        Console.Out.Flush();
        try { Console.TreatControlCAsInput = false; } catch { }
    }

    internal void SafeExit()
    {
        if (!_inFullScreen) return;
        _inFullScreen = false;
        _painter.StopPaintThread();
        try
        {
            _terminal.WriteAsync($"{AnsiPainter.E}[?1000l{AnsiPainter.E}[?1006l{AnsiPainter.E}[?25h{AnsiPainter.E}[?1049l{AnsiPainter.R}\n").GetAwaiter().GetResult();
            Console.Out.Flush();
            try { Console.TreatControlCAsInput = false; } catch { }
        }
        catch {}
    }

    public bool Verbose
    {
        get => _painter.Verbose;
        set => _painter.Verbose = value;
    }

    public void StartAssistantResponse()    => _painter.StartAssistantResponse();
    public void StreamText(string text)     => _painter.StreamText(text);
    public void EndAssistantResponse(int tokens = 0) => _painter.EndAssistantResponse(tokens);
    public void AppendThinking(string text) => _painter.AppendThinking(text);
    public void CollapseThinking(int n)     => _painter.CollapseThinking(n);
    public void ShowWaitingIndicator()      => _painter.ShowWaitingIndicator();
    public void ClearWaitingIndicator()     => _painter.ClearWaitingIndicator();

    public void WriteWelcome(string model, string endpoint) => _painter.WriteWelcome();
    public void WriteMarkdown(string md)                    => _painter.WriteMarkdown(md);
    public void WriteDebug(string message)                  {}
    public void WriteToolStart(string n, string a)          => _painter.WriteToolStart(n, a);
    public void WriteToolSuccess(string n)                  => _painter.WriteToolSuccess(n);
    public void WriteToolError(string n, string e)          => _painter.WriteToolError(n, e);
    public void WriteToolDenied(string n, string r)         => _painter.WriteToolDenied(n, r);
    public void WriteToolDiff(string diff)                  => _painter.WriteToolDiff(diff);
    public void WriteWarning(string m)                      => _painter.WriteWarning(m);
    public void WriteError(string m)                        => _painter.WriteError(m);
    public void WriteInfo(string m)                         => _painter.WriteInfo(m);
    public void WriteTodos(IReadOnlyList<TodoItem> todos)   {}
    public void ClearConversation()                         => _painter.ClearConversation();

    public void EnableCommandSuggestions(CommandRegistry registry)
        => _inputReader.EnableCommandSuggestions(registry);

    public string  ReadInput()                                   => _inputReader.ReadInput();
    public string? ShowCommandPicker(CommandRegistry registry)   => _inputReader.ShowCommandPicker(registry);

    public Task<string> AskUserAsync(string question, CancellationToken ct)
        => _inputReader.AskUserAsync(question, ct);

    public Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
        => _inputReader.AskPermissionAsync(toolName, summary, ct);

    public void BeginTurn()
    {
        _painter.ClearThinking();
        _inputReader.StartBackgroundInput();
        _painter.StartPaintThread();
    }

    public void EndTurn()
    {
        _painter.ClearThinking();
        _painter.ClearStreaming();
        _inputReader.StopBackgroundInput();
        _painter.StopPaintThread();
        _painter.Paint();
    }
}
