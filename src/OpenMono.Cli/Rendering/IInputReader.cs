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

    Task<string> AskUserAsync(string question, IReadOnlyList<string>? options, CancellationToken ct)
    {
        if (options is not { Count: > 0 })
            return AskUserAsync(question, ct);
        var numbered = string.Join("\n", options.Select((o, i) => $"  [{i + 1}] {o}"));
        return AskUserAsync($"{question}\n{numbered}", ct);
    }

    Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct);
    Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct);
}
