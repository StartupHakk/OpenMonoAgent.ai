using OpenMono.Session;

namespace OpenMono.Rendering;

public interface IOutputSink
{
    bool Verbose { get; set; }

    void StartAssistantResponse();
    void StreamText(string text);
    void EndAssistantResponse(int tokens = 0);

    void AppendThinking(string text);
    void CollapseThinking(int charCount);
    void ShowWaitingIndicator();
    void ClearWaitingIndicator();

    void WriteWelcome(string model, string endpoint);
    void WriteMarkdown(string markdown);
    void WriteDebug(string message);

    void WriteToolStart(string toolName, string args);
    void WriteToolSuccess(string toolName);
    void WriteToolError(string toolName, string error);
    void WriteToolDenied(string toolName, string reason);
    void WriteToolDiff(string diff);

    void WriteWarning(string message);
    void WriteError(string message);
    void WriteInfo(string message);

    void WriteTodos(IReadOnlyList<TodoItem> todos);
    void ClearConversation();
}
