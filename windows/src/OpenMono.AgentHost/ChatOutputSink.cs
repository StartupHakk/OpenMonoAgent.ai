using OpenMono.Permissions;
using OpenMono.Session;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Native permission choice surfaced by the WinUI 3 permission dialog.
/// Maps to OMA PermissionResponse: Once to Allow, Session to AllowAll,
/// Deny to Deny. (Deny for session is offered for destructive prompts.)
/// </summary>
public enum PermissionChoice
{
    AllowOnce,
    AllowForSession,
    Deny,
    DenyForSession,
}

/// <summary>
/// Chat message roles for the native chat window.
/// </summary>
public enum ChatRole
{
    User,
    Assistant,
    Thinking,
    Tool,
    System,
}

public sealed record ChatMessage(ChatRole Role, string Text, DateTime At, string? ToolName = null);

public sealed record ToolCallCard(
    string ToolName,
    string Summary,
    string Status,
    string? Detail,
    DateTime At);

/// <summary>
/// IOutputSink implementation that raises UI events instead of painting a
/// terminal. The WinUI 3 chat page subscribes and renders streaming text,
/// collapsible thinking, tool call cards, and status updates.
/// </summary>
public sealed class ChatOutputSink : OpenMono.Rendering.IOutputSink
{
    public bool Verbose { get; set; }

    public event Action<string>? AssistantStarted;
    public event Action<string>? TextStreamed;
    public event Action<OpenMono.Rendering.TurnMetrics?>? AssistantEnded;
    public event Action<string>? ThinkingAppended;
    public event Action<int>? ThinkingCollapsed;
    public event Action<ToolCallCard>? ToolCard;
    public event Action<ChatMessage>? Message;

    public void StartAssistantResponse() => AssistantStarted?.Invoke(string.Empty);

    public void StreamText(string text) => TextStreamed?.Invoke(text);

    public void EndAssistantResponse(OpenMono.Rendering.TurnMetrics? metrics = null) => AssistantEnded?.Invoke(metrics);

    public void AppendThinking(string text, string? agentLabel)
    {
        ThinkingAppended?.Invoke(text);
        Message?.Invoke(new ChatMessage(ChatRole.Thinking, text, DateTime.UtcNow));
    }

    public void CollapseThinking(int charCount, string? agentLabel) => ThinkingCollapsed?.Invoke(charCount);

    public void ShowWaitingIndicator(string? label, string? agentLabel) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, label ?? "Working.", DateTime.UtcNow));

    public void ClearWaitingIndicator(string? agentLabel)
    {
    }

    public void ShowToolProgress(string label) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, label, DateTime.UtcNow));

    public void ClearToolProgress()
    {
    }

    public void WriteWelcome(string model, string endpoint) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, $"Connected to {model} at {endpoint}.", DateTime.UtcNow));

    public void WriteMarkdown(string markdown) =>
        Message?.Invoke(new ChatMessage(ChatRole.Assistant, markdown, DateTime.UtcNow));

    public void WriteDebug(string message)
    {
        if (Verbose)
        {
            Message?.Invoke(new ChatMessage(ChatRole.System, message, DateTime.UtcNow));
        }
    }

    public void WriteToolStart(string toolName, string args)
    {
        var card = new ToolCallCard(toolName, args, "running", null, DateTime.UtcNow);
        ToolCard?.Invoke(card);
        Message?.Invoke(new ChatMessage(ChatRole.Tool, $"{toolName}: {args}", DateTime.UtcNow, toolName));
    }

    public void WriteToolSuccess(string toolName) =>
        ToolCard?.Invoke(new ToolCallCard(toolName, string.Empty, "success", null, DateTime.UtcNow));

    public void WriteToolError(string toolName, string error) =>
        ToolCard?.Invoke(new ToolCallCard(toolName, error, "error", error, DateTime.UtcNow));

    public void WriteToolDenied(string toolName, string reason) =>
        ToolCard?.Invoke(new ToolCallCard(toolName, reason, "denied", reason, DateTime.UtcNow));

    public void WriteToolDiff(string diff) =>
        Message?.Invoke(new ChatMessage(ChatRole.Tool, diff, DateTime.UtcNow));

    public void WriteWarning(string message) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, message, DateTime.UtcNow));

    public void WriteError(string message) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, message, DateTime.UtcNow));

    public void WriteInfo(string message) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, message, DateTime.UtcNow));

    public void WriteTodos(IReadOnlyList<TodoItem> todos) =>
        Message?.Invoke(new ChatMessage(ChatRole.System, string.Join("\n", todos.Select(t => $"[{t.Status}] {t.Content}")), DateTime.UtcNow));

    public void ClearConversation()
    {
    }
}
