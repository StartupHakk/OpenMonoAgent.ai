using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tui.Components;
using OpenMono.Tui.Keybindings;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OpenMono.Tui;

public sealed class TuiRenderer : IRenderer, ITuiEventSink
{
    private readonly AppConfig _config;

    private IApplication? _app;
    private Window? _window;
    private HeaderBar? _headerBar;
    private ConversationPane? _conversationPane;
    private MetricsStatusBar? _metricsBar;
    private InputWidget? _inputWidget;
    private ToolMonitorPanel? _toolMonitor;
    private ThinkingPanel? _thinkingPanel;

    private readonly SemaphoreSlim _inputReady = new(0, 1);
    private string _lastInput = "";
    private bool _cancelled;

    private CommandRegistry? _commands;
    private KeybindingManager? _keybindings;
    private int _toolIdCounter;

    private bool _sidebarVisible;
    private bool _approvalMode;
    private bool _showThinking = true;

    public bool Verbose { get; set; }

    public PauseController PauseController { get; } = new();

    public ApprovalController ApprovalController { get; } = new();

    public TuiRenderer(AppConfig config)
    {
        _config = config;
    }

    public void OnStreamStart()
    {
        Invoke(() =>
        {
            _metricsBar?.OnStreamStart();
            _conversationPane?.StreamStart();
        });
    }

    public void OnTokenReceived(string token, int totalCompletionTokens)
    {
        Invoke(() =>
        {
            _metricsBar?.OnTokenReceived(totalCompletionTokens);
            _conversationPane?.StreamToken(token);
        });
    }

    public void OnStreamEnd()
    {
        Invoke(() =>
        {
            _metricsBar?.OnStreamEnd();
            _conversationPane?.StreamEnd();
        });
    }

    public void OnToolStarted(string toolId, string toolName, string args)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.Tool,
                ToolName = toolName,
                ToolCallId = toolId,
                Content = $"▶ {toolName} {(args.Length > 80 ? args[..80] + "..." : args)}"
            });
            _toolMonitor?.ToolStarted(toolId, toolName, args);
        });
    }

    public void OnToolCompleted(string toolId, bool success, string? error)
    {
        Invoke(() =>
        {
            var icon = success ? "✓" : "✗";
            var content = error is not null ? $"{icon} {error}" : $"{icon} Done";
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = toolId,
                Content = content
            });
            _toolMonitor?.ToolCompleted(toolId, success, error);
        });
    }

    public void OnMessageAdded(Message message)
    {
        Invoke(() => _conversationPane?.AppendMessage(message));
    }

    public void UpdateMetrics(int promptTokens, int completionTokens, double tokensPerSec)
    {
        Invoke(() => _metricsBar?.UpdateContext(promptTokens));
    }

    public void EnableCommandSuggestions(CommandRegistry registry)
    {
        _commands = registry;
    }

    public string ReadInput()
    {
        _cancelled = false;
        _inputReady.Wait();

        if (_cancelled)
            throw new OperationCanceledException();

        return _lastInput;
    }

    public string? ShowCommandPicker(CommandRegistry registry)
    {

        return null;
    }

    public void WriteThinking()
    {
        Invoke(() => _thinkingPanel?.AppendThinking("Thinking...\n"));
    }

    public void ClearThinking()
    {
        Invoke(() => _thinkingPanel?.ClearThinking());
    }

    public void StartAssistantResponse()
    {
        ClearThinking();
        OnStreamStart();
    }

    public void StreamText(string text)
    {
        OnTokenReceived(text, 0);
    }

    public void EndAssistantResponse(int tokens = 0)
    {
        OnStreamEnd();
    }

    public void WriteWelcome(string model, string endpoint)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = $"OpenMono.ai — Model: {model} | Endpoint: {endpoint}\nType your request, or /help for commands."
            });
        });
    }

    public void WriteMarkdown(string markdown)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.Assistant,
                Content = markdown
            });
        });
    }

    public void WriteDebug(string message)
    {
        if (!Verbose) return;
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = $"[debug] {message}"
            });
        });
    }

    public void WriteToolStart(string toolName, string args)
    {
        var id = Interlocked.Increment(ref _toolIdCounter).ToString();
        OnToolStarted(id, toolName, args);
    }

    public void WriteToolSuccess(string toolName)
    {
        OnToolCompleted("", true, null);
    }

    public void WriteToolError(string toolName, string error)
    {
        OnToolCompleted("", false, $"{toolName}: {error}");
    }

    public void WriteToolDenied(string toolName, string reason)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.Tool,
                ToolName = toolName,
                Content = $"⊘ {reason}"
            });
        });
    }

    public void WriteWarning(string message)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = $"⚠ {message}"
            });
        });
    }

    public void WriteError(string message)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = $"Error: {message}"
            });
        });
    }

    public void WriteInfo(string message)
    {
        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = message
            });
        });
    }

    public async Task<string> AskUserAsync(string question, CancellationToken ct)
    {

        WriteInfo($"? {question}");
        _cancelled = false;
        await _inputReady.WaitAsync(ct);

        if (_cancelled)
            throw new OperationCanceledException();

        return _lastInput;
    }

    public async Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
    {

        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = $"Permission needed: {toolName}\n{summary}\n[y] Allow  [n] Deny  [a] Allow all  [!] Deny all"
            });
        });

        _cancelled = false;
        await _inputReady.WaitAsync(ct);

        if (_cancelled)
            throw new OperationCanceledException();

        return _lastInput.Trim().ToLowerInvariant() switch
        {
            "y" => PermissionResponse.Allow,
            "n" => PermissionResponse.Deny,
            "a" => PermissionResponse.AllowAll,
            "!" => PermissionResponse.DenyAll,
            _ => PermissionResponse.Allow,
        };
    }

    public void WriteTodos(IReadOnlyList<TodoItem> todos)
    {
        if (todos.Count == 0) return;

        var lines = new List<string> { "Tasks:" };
        foreach (var todo in todos)
        {
            var icon = todo.Status switch
            {
                "completed" => "✓",
                "in_progress" => "►",
                _ => "○",
            };
            var text = todo.Status == "in_progress" && todo.ActiveForm is not null
                ? todo.ActiveForm
                : todo.Content;
            lines.Add($"  {icon} {text}");
        }

        Invoke(() =>
        {
            _conversationPane?.AppendMessage(new Message
            {
                Role = MessageRole.System,
                Content = string.Join('\n', lines)
            });
        });
    }

    public void AppendThinking(string text)
    {
        Invoke(() => _thinkingPanel?.AppendThinking(text));
    }

    public void CollapseThinking(int charCount)
    {
        ClearThinking();
    }

    public void ShowWaitingIndicator() { }
    public void ClearWaitingIndicator() { }
    public void WriteToolDiff(string diff) { }
    public void ClearConversation() { }
    public void BeginTurn() { }
    public void EndTurn() { }

    public void RunTui(SessionState session, Action onReady)
    {
        using var app = Application.Create();
        app.Init();
        _app = app;

        var configPath = Path.Combine(_config.DataDirectory, "tui.json");
        Rendering.ThemeManager.Load(configPath);
        _keybindings = new KeybindingManager(configPath);

        _headerBar = new HeaderBar(app, _config.Llm.Model, _config.Llm.Endpoint);
        _metricsBar = new MetricsStatusBar(app, _keybindings, _config.Llm.ContextSize);

        _toolMonitor = new ToolMonitorPanel
        {
            X = Pos.AnchorEnd(40),
            Y = 1,
            Height = Dim.Fill(Dim.Absolute(3)),
            Visible = false
        };

        _thinkingPanel = new ThinkingPanel { AutoShow = _showThinking };

        _conversationPane = new ConversationPane
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(Dim.Absolute(3))
        };

        _inputWidget = new InputWidget(_commands ?? new CommandRegistry())
        {
            X = 0,
            Y = Pos.Bottom(_conversationPane),
            Width = Dim.Fill(),
        };

        _inputWidget.OnSubmit += (_, text) =>
        {

            _conversationPane.AppendMessage(new Message
            {
                Role = MessageRole.User,
                Content = text
            });

            _lastInput = text;
            _inputReady.Release();
        };

        _inputWidget.OnCancel += (_, _) =>
        {
            _cancelled = true;
            _inputReady.Release();
        };

        var window = new Window
        {
            Title = "OpenMono.ai",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _window = window;

        window.Add(_headerBar, _conversationPane, _toolMonitor, _thinkingPanel, _inputWidget, _metricsBar);

        _metricsBar.X = 0;
        _metricsBar.Y = Pos.AnchorEnd(1);
        _metricsBar.Width = Dim.Fill();

        window.FrameChanged += (_, _) =>
        {
            if (_sidebarVisible && window.Frame.Size.Width < 120)
            {
                _sidebarVisible = false;
                ApplySidebarLayout();
            }
        };

        app.Keyboard.KeyDown += (_, key) =>
        {
            var action = _keybindings.Resolve(key);
            if (action is null)
                return;

            key.Handled = true;
            HandleAction(action.Value);
        };

        ApprovalController.RequestApprovalFunc = (call, ct) =>
        {
            var tcs = new TaskCompletionSource<ApprovalDecision>();
            app.Invoke(() =>
            {
                var decision = ApprovalDialog.Show(app, call);
                tcs.TrySetResult(decision);
            });
            return tcs.Task;
        };

        app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            onReady();
            return false;
        });

        app.Run(window);
        _app = null;
    }

    private void HandleAction(TuiAction action)
    {
        switch (action)
        {
            case TuiAction.Pause:
                PauseController.TogglePause();
                var paused = PauseController.IsPaused;
                _metricsBar?.SetPaused(paused);
                if (_conversationPane is not null)
                    _conversationPane.AutoScrollEnabled = !paused;
                _conversationPane?.AppendMessage(new Message
                {
                    Role = MessageRole.System,
                    Content = paused ? "⏸ Paused — scroll to review, press Ctrl+P to resume" : "▶ Resumed"
                });
                break;

            case TuiAction.ToggleSidebar:
                _sidebarVisible = !_sidebarVisible;
                ApplySidebarLayout();
                break;

            case TuiAction.ToggleApproval:
                ApprovalController.ToggleApprovalMode();
                _approvalMode = ApprovalController.ManualApprovalMode;
                _metricsBar?.SetApprovalMode(_approvalMode);
                _conversationPane?.AppendMessage(new Message
                {
                    Role = MessageRole.System,
                    Content = _approvalMode
                        ? "Approval mode: ON — all tool calls require confirmation"
                        : "Approval mode: OFF — using configured permissions"
                });
                break;

            case TuiAction.ToggleThinking:
                _showThinking = !_showThinking;
                if (_thinkingPanel is not null)
                {
                    _thinkingPanel.AutoShow = _showThinking;
                    _thinkingPanel.ToggleVisibility();
                }
                break;

            case TuiAction.Help:
                ShowKeybindingHelp();
                break;

            case TuiAction.Debug:
                Verbose = !Verbose;
                _conversationPane?.AppendMessage(new Message
                {
                    Role = MessageRole.System,
                    Content = $"Debug mode: {(Verbose ? "ON" : "OFF")}"
                });
                break;
        }
    }

    private void ShowKeybindingHelp()
    {
        if (_keybindings is null || _app is null) return;

        HelpOverlay.Show(_app, _keybindings, _commands);
    }

    private void ApplySidebarLayout()
    {
        if (_toolMonitor is null || _conversationPane is null)
            return;

        _toolMonitor.Visible = _sidebarVisible;
        _conversationPane.Width = _sidebarVisible
            ? Dim.Fill(Dim.Absolute(40))
            : Dim.Fill();

        _window?.SetNeedsLayout();
    }

    private void Invoke(Action action)
    {
        if (_app is { } app)
        {
            app.Invoke(action);
        }

    }
}
