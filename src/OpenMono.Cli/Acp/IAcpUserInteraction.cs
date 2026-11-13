using OpenMono.Playbooks;

namespace OpenMono.Acp;

/// <summary>
/// Structured metadata for a permission request, so a client can render a
/// DISTINCT card variant without re-parsing the summary string. Elevated =
/// a root (sudo) host execution: the card is the elevation consent, and an
/// allow must carry the user's sudo password back on the permission response.
/// </summary>
public sealed record PermissionRequestContext(bool Elevated, string? Command = null);

public interface IAcpUserInteraction
{
    /// <summary>
    /// Request permission to execute a tool.
    /// Returns (Allow: bool, Scope: "once"|"session")
    /// - "once" = temporary grant for this tool call only
    /// - "session" = persistent grant for remainder of session
    /// </summary>
    Task<(bool Allow, string Scope)> RequestPermissionAsync(
        string toolName, string summary, bool dangerous, CancellationToken ct,
        PermissionRequestContext? context = null);

    Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct);

    Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct);

    Task<string?> RequestUserInputAsync(string question, CancellationToken ct);
}
