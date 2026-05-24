using System.Text;
using OpenMono.Session;

namespace OpenMono.Rendering;

internal sealed class SubAgentOutputSink(string agentDescription, Action<string> parentWriteOutput) : IOutputSink
{
    private readonly StringBuilder _buffer = new();

    public string CapturedText => _buffer.ToString();

    public bool Verbose { get; set; }

    public void StartAssistantResponse() { }
    public void StreamText(string text) => _buffer.Append(text);
    public void EndAssistantResponse(TurnMetrics? metrics = null) { }

    public void WriteToolStart(string toolName, string args)
        => parentWriteOutput($"  [Agent: {agentDescription}] → {toolName}");
    public void WriteToolSuccess(string toolName) { }
    public void WriteToolError(string toolName, string error)
        => parentWriteOutput($"  [Agent: {agentDescription}] ✗ {toolName}: {error}");
    public void WriteToolDenied(string toolName, string reason)
        => parentWriteOutput($"  [Agent: {agentDescription}] ✗ {toolName}: permission denied");

    public void WriteWarning(string message) => parentWriteOutput($"  [Agent: {agentDescription}] ⚠ {message}");
    public void WriteError(string message) => parentWriteOutput($"  [Agent: {agentDescription}] ✗ {message}");
    public void WriteInfo(string message) => parentWriteOutput($"  [Agent: {agentDescription}] {message}");

    public void AppendThinking(string text) { }
    public void CollapseThinking(int charCount) { }
    public void ShowWaitingIndicator() { }
    public void ClearWaitingIndicator() { }
    public void WriteWelcome(string model, string endpoint) { }
    public void WriteMarkdown(string markdown) => _buffer.Append(markdown);
    public void WriteDebug(string message) { }
    public void WriteToolDiff(string diff) { }
    public void WriteTodos(IReadOnlyList<TodoItem> todos) { }
    public void ClearConversation() { }
}
