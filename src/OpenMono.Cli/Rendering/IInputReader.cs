using OpenMono.Commands;
using OpenMono.Permissions;
using OpenMono.Playbooks;

namespace OpenMono.Rendering;

public interface IInputReader
{
    void EnableCommandSuggestions(CommandRegistry registry);
    string ReadInput();
    string? ShowCommandPicker(CommandRegistry registry);
    Task<string> AskUserAsync(string question, CancellationToken ct);
    Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct);
    Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct);
}
