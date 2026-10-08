using OpenMono.Commands;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// IInputReader implementation backed by native WinUI 3 dialogs. Permission
/// prompts resolve through the injected chooser (Allow once, Allow for session,
/// Deny), AskUser prompts resolve through the injected asker, and the slash
/// command palette resolves through the injected palette.
/// </summary>
public sealed class ChatInputReader : IInputReader
{
    private readonly Func<string, string, CancellationToken, Task<PermissionChoice>> _choosePermission;
    private readonly Func<string, IReadOnlyList<string>?, CancellationToken, Task<string>> _askUser;
    private readonly Func<CommandRegistry, CancellationToken, Task<string?>>? _palette;

    public ChatInputReader(
        Func<string, string, CancellationToken, Task<PermissionChoice>> choosePermission,
        Func<string, IReadOnlyList<string>?, CancellationToken, Task<string>> askUser,
        Func<CommandRegistry, CancellationToken, Task<string?>>? palette = null)
    {
        _choosePermission = choosePermission;
        _askUser = askUser;
        _palette = palette;
    }

    public void EnableCommandSuggestions(CommandRegistry registry)
    {
    }

    public string ReadInput() => string.Empty;

    public string? ShowCommandPicker(CommandRegistry registry) =>
        _palette is null ? null : _palette(registry, CancellationToken.None).GetAwaiter().GetResult();

    public Task<string> AskUserAsync(string question, CancellationToken ct) =>
        _askUser(question, null, ct);

    public Task<string> AskUserAsync(string question, IReadOnlyList<string>? options, CancellationToken ct) =>
        _askUser(question, options, ct);

    public async Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
    {
        var choice = await _choosePermission(toolName, summary, ct);
        return choice switch
        {
            PermissionChoice.AllowOnce => PermissionResponse.Allow,
            PermissionChoice.AllowForSession => PermissionResponse.AllowAll,
            PermissionChoice.DenyForSession => PermissionResponse.DenyAll,
            _ => PermissionResponse.Deny,
        };
    }

    public Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct) =>
        Task.FromResult(false);
}
