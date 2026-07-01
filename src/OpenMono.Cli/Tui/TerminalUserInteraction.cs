using OpenMono.Acp;
using OpenMono.Permissions;
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
