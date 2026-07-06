using OpenMono.Playbooks;

namespace OpenMono.Acp;




public interface IAcpUserInteraction
{
    /// <summary>
    /// Request permission to execute a tool.
    /// Returns (Allow: bool, Scope: "once"|"session")
    /// - "once" = temporary grant for this tool call only
    /// - "session" = persistent grant for remainder of session
    /// </summary>
    Task<(bool Allow, string Scope)> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct);

    Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct);

    Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct);

    Task<string?> RequestUserInputAsync(string question, CancellationToken ct);
}
