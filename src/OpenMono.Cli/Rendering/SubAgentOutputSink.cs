using System.Text;
using OpenMono.Session;

namespace OpenMono.Rendering;

internal sealed class SubAgentOutputSink(
    string agentDescription,
    Action<string> parentWriteOutput,
    IOutputSink? parentSink = null) : IOutputSink
{
    private readonly StringBuilder _buffer = new();
    private readonly string _labelPrefix = $"Agent: {agentDescription}";

    public string CapturedText => _buffer.ToString();

    public bool Verbose { get; set; }

    public void StartAssistantResponse() { }
    public void StreamText(string text) => _buffer.Append(text);
    public void EndAssistantResponse(TurnMetrics? metrics = null) { }

    public void WriteToolStart(string toolName, string args)
    {
        parentWriteOutput($"  [{_labelPrefix}] → {toolName}");
        parentSink?.ShowWaitingIndicator($"{_labelPrefix} · {toolName}");
    }
    public void WriteToolSuccess(string toolName)
        => parentWriteOutput($"  [{_labelPrefix}] ✓ {toolName}");
    public void WriteToolError(string toolName, string error)
        => parentWriteOutput($"  [{_labelPrefix}] ✗ {toolName}: {error}");
    public void WriteToolDenied(string toolName, string reason)
        => parentWriteOutput($"  [{_labelPrefix}] ⊘ {toolName}: permission denied");

    public void WriteWarning(string message) => parentWriteOutput($"  [{_labelPrefix}] ⚠ {message}");
    public void WriteError(string message) => parentWriteOutput($"  [{_labelPrefix}] ✗ {message}");
    public void WriteInfo(string message) => parentWriteOutput($"  [{_labelPrefix}] {message}");

    public void AppendThinking(string text) { }
    public void CollapseThinking(int charCount) { }
    public void ShowWaitingIndicator(string? label = null)
        => parentSink?.ShowWaitingIndicator(string.IsNullOrEmpty(label) ? $"{_labelPrefix} · thinking" : $"{_labelPrefix} · {label}");
    public void ClearWaitingIndicator() => parentSink?.ClearWaitingIndicator();
    public void WriteWelcome(string model, string endpoint) { }
    public void WriteMarkdown(string markdown) => _buffer.Append(markdown);
    public void WriteDebug(string message) { }
    public void WriteToolDiff(string diff) { }
    public void WriteTodos(IReadOnlyList<TodoItem> todos) { }
    public void ClearConversation() { }
}
