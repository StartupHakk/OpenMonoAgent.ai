using OpenMono.Commands;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;
using OpenMono.Session;

namespace OpenMono.Tests.Rendering;

/// <summary>
/// An IRenderer that answers prompts from a scripted queue (FIFO) and records
/// everything written, so wizard-style commands can be tested end to end.
/// </summary>
internal sealed class FakeScriptedRenderer : IRenderer
{
    private readonly Queue<string> _answers = new();

    public bool Verbose { get; set; }
    public bool Interactive { get; set; } = true;
    public string[] Written { get; } = [];
    private readonly List<string> _written = [];

    public void Enqueue(params string[] answers)
    {
        foreach (var a in answers) _answers.Enqueue(a);
    }

    public string? DequeueWritten()
    {
        if (_written.Count == 0) return null;
        var line = _written[0];
        _written.RemoveAt(0);
        return line;
    }

    public bool Wrote(string fragment) => _written.Any(l => l.Contains(fragment, StringComparison.Ordinal));

    // IInputReader
    public void EnableCommandSuggestions(CommandRegistry registry) { }
    public string ReadInput() => string.Empty;
    public string? ShowCommandPicker(CommandRegistry registry) => null;

    public Task<string> AskUserAsync(string question, CancellationToken ct)
        => Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : "");

    public Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
        => Task.FromResult(PermissionResponse.Allow);

    public Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct)
        => Task.FromResult(true);

    public Task<char[]> AskSecretAsync(string question, CancellationToken ct)
        => Task.FromResult((_answers.Count > 0 ? _answers.Dequeue() : "").ToCharArray());

    public Task<string?> SelectOptionAsync(string title, IReadOnlyList<string> options, int initial, CancellationToken ct)
    {
        var a = (_answers.Count > 0 ? _answers.Dequeue() : "").Trim();
        if (a.Length == 0) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(int.TryParse(a, out var idx) && idx >= 1 && idx <= options.Count ? options[idx - 1] : a);
    }

    // IOutputSink
    public void StartAssistantResponse() { }
    public void StreamText(string text) { }
    public void EndAssistantResponse(TurnMetrics? metrics = null) { }
    public void AppendThinking(string text, string? agentLabel) { }
    public void CollapseThinking(int charCount, string? agentLabel) { }
    public void ShowWaitingIndicator(string? label, string? agentLabel) { }
    public void ClearWaitingIndicator(string? agentLabel) { }
    public void ShowToolProgress(string label) { }
    public void ClearToolProgress() { }
    public void WriteWelcome(string model, string endpoint) { }
    public void WriteMarkdown(string markdown) { _written.Add(markdown); }
    public void WriteDebug(string message) { }
    public void WriteToolStart(string toolName, string args) { }
    public void WriteToolSuccess(string toolName) { }
    public void WriteToolError(string toolName, string error) { }
    public void WriteToolDenied(string toolName, string reason) { }
    public void WriteToolDiff(string diff) { }
    public void WriteToolContent(string toolName, string filePath, string content) { }
    public void WriteWarning(string message) { _written.Add($"[warn] {message}"); }
    public void WriteError(string message) { _written.Add($"[error] {message}"); }
    public void WriteInfo(string message) { _written.Add(message); }
    public void WriteTodos(IReadOnlyList<TodoItem> todos) { }
    public void ClearConversation() { }

    // ILiveFeedback
    public void BeginTurn() { }
    public void EndTurn() { }
}
