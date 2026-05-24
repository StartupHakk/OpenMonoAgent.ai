using OpenMono.Commands;
using OpenMono.Permissions;

namespace OpenMono.Rendering;

internal sealed class NullInputReader : IInputReader
{
    public void EnableCommandSuggestions(CommandRegistry registry) { }

    public string ReadInput() => string.Empty;

    public string? ShowCommandPicker(CommandRegistry registry) => null;

    public Task<string> AskUserAsync(string question, CancellationToken ct) =>
        Task.FromResult("[Sub-agent cannot ask user questions. Make a decision based on available information and continue.]");

    public Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct) =>
        Task.FromResult(PermissionResponse.Deny);
}
