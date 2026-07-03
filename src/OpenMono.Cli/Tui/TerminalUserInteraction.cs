using OpenMono.Acp;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;

namespace OpenMono.Tui;









public sealed class TerminalUserInteraction : IAcpUserInteraction
{
    private readonly IInputReader _input;

    public TerminalUserInteraction(IInputReader input)
    {
        _input = input;
    }

    public async Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
    {
        var response = await _input.AskPermissionAsync(toolName, summary, ct);
        return response is PermissionResponse.Allow or PermissionResponse.AllowAll;
    }

    public async Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct)
    {
        var steps = string.Join("\n  ", plan.Steps.Select(s => $"- {s.Id} (gate: {s.Gate})"));
        var tools = plan.Tools.Count > 0
            ? string.Join(", ", plan.Tools.Select(t => t.Dangerous ? $"{t.Name}*" : t.Name))
            : "(no tools)";
        var question = $"Playbook '{plan.PlaybookName}' will run these steps:\n  {steps}\n\nAllowed tools: {tools}";
        if (plan.RequiresModeSwitch)
            question += "\nNote: this will also switch you from Plan mode to Build mode.";
        question += "\nApprove? [y/N]";
        var answer = await _input.AskUserAsync(question, ct);
        return answer?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true || answer?.Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct)
    {
        var answer = await _input.AskUserAsync($"Switch to Build mode? {reason}", ct);
        return answer?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task<string?> RequestUserInputAsync(string question, CancellationToken ct)
    {
        var answer = await _input.AskUserAsync(question, ct);
        return answer;
    }
}
