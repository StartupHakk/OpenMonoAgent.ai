using OpenMono.Session;

namespace OpenMono.Rendering;

public sealed record TurnMetrics
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public TimeSpan TimeToFirstToken { get; init; }
    public TimeSpan TotalElapsed { get; init; }
}

public interface IOutputSink
{
    bool Verbose { get; set; }

    void StartAssistantResponse();
    void StreamText(string text);
    void EndAssistantResponse(TurnMetrics? metrics = null);

    void AppendThinking(string text) => AppendThinking(text, null);
    void AppendThinking(string text, string? agentLabel);
    void CollapseThinking(int charCount) => CollapseThinking(charCount, null);
    void CollapseThinking(int charCount, string? agentLabel);
    void ShowWaitingIndicator(string? label = null) => ShowWaitingIndicator(label, null);
    void ShowWaitingIndicator(string? label, string? agentLabel);
    void ClearWaitingIndicator() => ClearWaitingIndicator(null);
    void ClearWaitingIndicator(string? agentLabel);

    void WriteWelcome(string model, string endpoint);
    void WriteMarkdown(string markdown);
    void WriteDebug(string message);

    void WriteToolStart(string toolName, string args);
    void WriteToolSuccess(string toolName);
    void WriteToolError(string toolName, string error);
    void WriteToolDenied(string toolName, string reason);
    void WriteToolDiff(string diff);
    void WriteToolContent(string toolName, string filePath, string content) { }

    void WriteWarning(string message);
    void WriteError(string message);
    void WriteInfo(string message);

    void WriteTodos(IReadOnlyList<TodoItem> todos);
    void ClearConversation();
}
