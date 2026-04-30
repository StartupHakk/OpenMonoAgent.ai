using System.Diagnostics;
using Spectre.Console;
using OpenMono.Commands;
using OpenMono.Permissions;

namespace OpenMono.Rendering;

public sealed class TerminalRenderer : IRenderer
{
    private readonly IAnsiConsole _console;
    private bool _inAssistantResponse;
    private CancellationTokenSource? _thinkingCts;
    private Task? _thinkingTask;
    private bool _thinkingActive;
    private int _thinkingChars;
    private readonly Stopwatch _streamStopwatch = new();
    private int _streamTokenCount;
    private bool _streamAtLineStart;

    public bool Verbose { get; set; }

    private CommandAwareInput? _commandInput;

    public TerminalRenderer(IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
    }

    public void EnableCommandSuggestions(CommandRegistry registry)
    {
        if (Console.IsInputRedirected
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            || File.Exists("/.dockerenv"))
            return;

        _commandInput = new CommandAwareInput(registry);
    }

    public string ReadInput()
    {
        if (_commandInput is not null)
        {
            try
            {
                return _commandInput.Read();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Console.Write("\r\x1b[K");
                _commandInput = null;
            }
        }

        return _console.Prompt(
            new TextPrompt<string>("[bold green]>[/] ")
                .AllowEmpty());
    }

    public string? ShowCommandPicker(CommandRegistry registry)
    {
        const string cancelChoice = "[dim]← Cancel[/]";

        var formattedChoices = new List<string> { cancelChoice };

        foreach (var cmd in registry.All.OrderBy(c => c.Name))
        {
            var name = cmd.Name.TrimStart('/');
            formattedChoices.Add($"/{name,-12} [dim]{Markup.Escape(cmd.Description)}[/]");
        }

        formattedChoices.Add($"/{"quit",-12} [dim]Exit OpenMono[/]");

        var prompt = new SelectionPrompt<string>()
            .Title("[bold green]Commands[/] [dim](↑↓ navigate, Enter select, Ctrl+C cancel)[/]")
            .PageSize(12)
            .HighlightStyle(new Style(Color.Green, decoration: Decoration.Bold))
            .AddChoices(formattedChoices);

        var selected = _console.Prompt(prompt);

        if (selected == cancelChoice) return null;

        return selected.Split(' ', 2)[0].Trim();
    }

    public void WriteWelcome(string model, string endpoint)
    {
        _console.WriteLine();

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddRow($"[bold green]OpenMono.ai[/] [dim]—[/] Local Coding Agent");
        grid.AddRow($"[dim]Model:[/] [cyan]{Markup.Escape(model)}[/] [dim]|[/] [dim]Endpoint:[/] [cyan]{Markup.Escape(endpoint)}[/]");
        grid.AddRow("[dim]Type your request, or /help for commands. Ctrl+C to clear · Ctrl+C twice to exit.[/]");

        var panel = new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Blue),
            Padding = new Padding(1, 0),
        };
        _console.Write(panel);
        _console.WriteLine();
    }

    public void ShowWaitingIndicator()
    {
        _thinkingCts = new CancellationTokenSource();
        var ct = _thinkingCts.Token;
        _thinkingTask = Task.Run(async () =>
        {
            var sequence = new[] { 1, 2, 3, 4, 3, 2 };
            var idx = 0;
            while (!ct.IsCancellationRequested)
            {
                var dots = new string('.', sequence[idx % sequence.Length]);
                Console.Write($"\r  \u001b[2;36m⠿ Thinking{dots}\u001b[0m\u001b[K");
                Console.Out.Flush();
                idx++;
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public void ClearWaitingIndicator()
    {
        if (_thinkingCts is not null)
        {
            _thinkingCts.Cancel();
            _thinkingCts.Dispose();
            _thinkingCts = null;
            _thinkingTask = null;
            Console.Write("\r\u001b[K");
            Console.Out.Flush();
        }
    }

    public void AppendThinking(string text)
    {
        if (!_thinkingActive)
        {

            Console.Write("\u001b[s");
            Console.Write("\n  \u001b[2;36m◈ Thinking\u001b[0m\n  \u001b[2;3;90m");
            _thinkingActive = true;
        }
        Console.Write(text);
        Console.Out.Flush();
        _thinkingChars += text.Length;
    }

    public void CollapseThinking(int charCount)
    {
        if (!_thinkingActive) return;
        Console.Write("\u001b[0m");
        Console.Write("\u001b[u\u001b[J");
        var approxTok = charCount / 4;
        var tok = approxTok > 0 ? $" [{approxTok} tok]" : "";
        Console.Write($"\n  \u001b[2;36m◈ Thinking{tok}\u001b[0m\n");
        Console.Out.Flush();
        _thinkingActive = false;
        _thinkingChars = 0;
    }

    private void ClearThinkingAnimation()
    {
        if (_thinkingCts is not null)
        {
            _thinkingCts.Cancel();
            try { _thinkingTask?.Wait(); } catch {}
            _thinkingCts.Dispose();
            _thinkingCts = null;
            _thinkingTask = null;
        }
    }

    public void StartAssistantResponse()
    {
        ClearThinkingAnimation();
        _inAssistantResponse = true;
        _streamStopwatch.Restart();
        _streamTokenCount = 0;
        _streamAtLineStart = true;

        Console.Write("\r\u001b[K");
        _console.MarkupLine("");
        _console.MarkupLine("  [bold green]◆ Assistant[/]");
        _console.MarkupLine("  [dim green]─────────────────────────────────────────────────[/]");
    }

    public void StreamText(string text)
    {
        ClearThinkingAnimation();
        _streamTokenCount++;

        for (var i = 0; i < text.Length; i++)
        {
            if (_streamAtLineStart)
            {
                Console.Write("    ");
                _streamAtLineStart = false;
            }

            Console.Write(text[i]);

            if (text[i] == '\n')
                _streamAtLineStart = true;
        }
    }

    public void EndAssistantResponse(int tokens = 0)
    {
        ClearThinkingAnimation();
        if (_inAssistantResponse)
        {
            _streamStopwatch.Stop();
            Console.WriteLine();

            var elapsed = _streamStopwatch.Elapsed;
            var tokSec = elapsed.TotalSeconds > 0 ? _streamTokenCount / elapsed.TotalSeconds : 0;
            _console.MarkupLine($"  [dim green]─────────────────────────────────────────────────[/]");
            _console.MarkupLine($"  [dim]{_streamTokenCount} chunks · {elapsed.TotalSeconds:F1}s · {tokSec:F0} tok/s[/]");
            _console.WriteLine();
        }
        _inAssistantResponse = false;
    }

    public void WriteMarkdown(string markdown)
    {
        _console.WriteLine(markdown);
    }

    public void WriteDebug(string message)
    {
        if (!Verbose) return;
        _console.MarkupLine($"  [dim cyan]⟐ {Markup.Escape(message)}[/]");
    }

    public void WriteToolStart(string toolName, string args)
    {
        var truncatedArgs = args.Length > 80 ? args[..80] + "…" : args;
        _console.MarkupLine($"  [dim]⧫[/] [bold grey]{Markup.Escape(toolName)}[/] [dim]{Markup.Escape(truncatedArgs)}[/]");
    }

    public void WriteToolSuccess(string toolName)
    {
        _console.MarkupLine($"  [green]✓[/] [dim]{Markup.Escape(toolName)}[/]");
    }

    public void WriteToolError(string toolName, string error)
    {
        _console.MarkupLine($"  [red]✗ {Markup.Escape(toolName)}[/][dim]: {Markup.Escape(error)}[/]");
    }

    public void WriteToolDenied(string toolName, string reason)
    {
        _console.MarkupLine($"  [yellow]⊘ {Markup.Escape(toolName)}[/][dim]: {Markup.Escape(reason)}[/]");
    }

    public void WriteWarning(string message)
    {
        _console.MarkupLine($"  [yellow]⚠ {Markup.Escape(message)}[/]");
    }

    public void WriteError(string message)
    {
        _console.MarkupLine($"  [red]✗ Error: {Markup.Escape(message)}[/]");
    }

    public void WriteInfo(string message)
    {
        _console.MarkupLine($"  [dim]{Markup.Escape(message)}[/]");
    }

    public Task<string> AskUserAsync(string question, CancellationToken ct)
    {
        _console.WriteLine();
        _console.MarkupLine($"  [bold yellow]? {Markup.Escape(question)}[/]");
        var answer = _console.Prompt(
            new TextPrompt<string>("  [yellow]>[/] ")
                .AllowEmpty());
        return Task.FromResult(answer);
    }

    public Task<PermissionResponse> AskPermissionAsync(
        string toolName, string summary, CancellationToken ct)
    {
        _console.WriteLine();
        var panel = new Panel(Markup.Escape(summary))
        {
            Header = new PanelHeader($" {toolName} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Yellow),
            Padding = new Padding(2, 1),
        };
        _console.Write(panel);

        _console.MarkupLine("  [dim][[y]] Allow  [[n]] Deny  [[a]] Allow all  [[!]] Deny all[/]");
        var key = _console.Prompt(
            new TextPrompt<string>("  [yellow]Permission?[/] ")
                .DefaultValue("y")
                .AddChoice("y").AddChoice("n").AddChoice("a").AddChoice("!"));

        return Task.FromResult(key switch
        {
            "y" => PermissionResponse.Allow,
            "n" => PermissionResponse.Deny,
            "a" => PermissionResponse.AllowAll,
            "!" => PermissionResponse.DenyAll,
            _ => PermissionResponse.Deny,
        });
    }

    public void WriteToolDiff(string diff) { }

    public void WriteTodos(IReadOnlyList<Session.TodoItem> todos)
    {
        if (todos.Count == 0) return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("").Width(3))
            .AddColumn(new TableColumn("Task"));

        foreach (var todo in todos)
        {
            var icon = todo.Status switch
            {
                "completed" => "[green]✓[/]",
                "in_progress" => "[yellow]►[/]",
                _ => "[dim]○[/]",
            };
            var text = todo.Status == "in_progress" && todo.ActiveForm is not null
                ? $"[yellow]{Markup.Escape(todo.ActiveForm)}[/]"
                : Markup.Escape(todo.Content);
            table.AddRow(icon, text);
        }

        _console.Write(table);
    }

    public void ClearConversation()
    {
        Console.Clear();
    }

    public void BeginTurn() { }
    public void EndTurn() { }
}
