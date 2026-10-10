namespace OpenMono.HostBridge;

/// <summary>
/// One playbook step as the ACP pause describes it (gate arrives as text).
/// </summary>
public sealed record PlaybookStep(string Id, string Gate, string? Description);

/// <summary>
/// One playbook tool as the ACP pause describes it.
/// </summary>
public sealed record PlaybookTool(string Name, bool IsReadOnly, bool Dangerous);

/// <summary>
/// How a turn is rendered and how the operator is asked. Line mode writes to
/// stdout/stderr; TUI mode drives the full-screen agent renderer. Bridge logic
/// stays identical — only this surface changes.
/// </summary>
public interface IBridgeUi
{
    void ResponseBegin();
    void ResponseEnd();
    void Text(string content);
    void Thinking(string content);
    void ToolStart(string name, string summary);
    void ToolEnd(string name, bool ok, string reason);
    void Usage(long contextTokens, long contextWindow);
    void Info(string message);
    void Error(string message);
    void Markdown(string markdown);
    void HostStart(string command);
    void HostDone(string command, string output);
    void PlaybookDecision(string name, bool allow);
    void ModeNotice(string mode);
    Task<bool> ConfirmAsync(string question, CancellationToken ct);
    Task<string> InputAsync(string question, CancellationToken ct);
    Task<bool> PlaybookAsync(string name, IReadOnlyList<PlaybookStep> steps, IReadOnlyList<PlaybookTool> tools, bool requiresModeSwitch, CancellationToken ct);
    Task<char[]> PasswordAsync(string prompt, CancellationToken ct);
}

/// <summary>
/// The plain scrolling interface: the bridge's original stdout/stderr surface.
/// </summary>
public sealed class LineUi : IBridgeUi
{
    private readonly bool _nonInteractive;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public LineUi(bool nonInteractive, TextWriter stdout, TextWriter stderr)
    {
        _nonInteractive = nonInteractive;
        _out = stdout;
        _err = stderr;
    }

    public void ResponseBegin() { }
    public void ResponseEnd() => _out.WriteLine();

    public void Text(string content) => _out.Write(content);
    public void Thinking(string content) => _err.Write(content);

    public void ToolStart(string name, string summary) =>
        _err.WriteLine($"\n[tool] {name} {summary}");

    public void ToolEnd(string name, bool ok, string reason) =>
        _err.WriteLine($"[tool] {name} {(ok ? "ok" : "FAILED")} {reason}".TrimEnd());

    public void Usage(long contextTokens, long contextWindow)
    {
        if (contextWindow > 0)
            _err.WriteLine($"[ctx] {contextTokens}/{contextWindow} tokens");
    }

    public void Info(string message) => _err.WriteLine(message);
    public void Error(string message) => _err.WriteLine(message);
    public void Markdown(string markdown) => _out.WriteLine(markdown);

    public void HostStart(string command) => _err.WriteLine($"[host] $ {command}");

    public void HostDone(string command, string output) =>
        _err.WriteLine($"[host] done ({output.Length} chars)");

    public void PlaybookDecision(string name, bool allow) =>
        _err.WriteLine($"[bridge] playbook {(allow ? "approved" : "denied")}.");

    public void ModeNotice(string mode) => _err.WriteLine($"\n[mode] now: {mode}");

    public Task<bool> ConfirmAsync(string question, CancellationToken ct)
    {
        if (_nonInteractive)
        {
            _err.WriteLine($"[bridge] --non-interactive: answering No: {question}");
            return Task.FromResult(false);
        }
        _err.Write($"{question} [y/N] ");
        var answer = Console.ReadLine();
        return Task.FromResult(answer is not null &&
            (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
             answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)));
    }

    public Task<string> InputAsync(string question, CancellationToken ct)
    {
        _err.WriteLine($"\n[agent asks] {question}");
        _err.Write("> ");
        return Task.FromResult(Console.ReadLine() ?? "");
    }

    public Task<bool> PlaybookAsync(
        string name,
        IReadOnlyList<PlaybookStep> steps,
        IReadOnlyList<PlaybookTool> tools,
        bool requiresModeSwitch,
        CancellationToken ct)
    {
        var stepLines = string.Join("\n  ", steps.Select(s => $"- {s.Id} (gate: {s.Gate})"));
        var toolList = tools.Count > 0
            ? string.Join(", ", tools.Select(t => t.Dangerous ? $"{t.Name}*" : t.Name))
            : "(no tools)";
        var question = $"Playbook '{name}' will run these steps:\n  {stepLines}\n\nAllowed tools: {toolList}";
        if (requiresModeSwitch)
            question += "\nNote: this will also switch you from Plan mode to Build mode.";
        return ConfirmAsync(question, ct);
    }

    public Task<char[]> PasswordAsync(string prompt, CancellationToken ct) =>
        Task.FromResult(HostIdentity.PromptPassword(prompt, _err));
}
