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

    /// <summary>
    /// True when the renderer can interactively prompt (a TUI). Commands that
    /// need to walk the user through input (e.g. the /git add wizard) must
    /// bail out early instead of firing prompts that a redirected terminal
    /// would answer with placeholders.
    /// </summary>
    bool Interactive => false;

    /// <summary>
    /// Masked prompt for secrets (PATs etc.): echoes stars only and never
    /// writes the value to the transcript. Return value is owned by the
    /// caller (zero it or hand it on). Implementations that cannot mask
    /// should fall back to a regular prompt only when <see cref="Interactive"/>.
    /// </summary>
    Task<char[]> AskSecretAsync(string question, CancellationToken ct)
        => Task.FromResult(Array.Empty<char>());

    Task<string> AskUserAsync(string question, IReadOnlyList<string>? options, CancellationToken ct)
    {
        if (options is not { Count: > 0 })
            return AskUserAsync(question, ct);
        var numbered = string.Join("\n", options.Select((o, i) => $"  [{i + 1}] {o}"));
        return AskUserAsync($"{question}\n{numbered}", ct);
    }

    Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct);
    Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct);

    /// <summary>
    /// Standardized selection menu: present a list of options, navigate with the user's
    /// choice, return the selected option or <c>null</c> to cancel. Every renderer must
    /// implement this explicitly (TUI: input-lane overlay; terminal: numbered prompt).
    /// </summary>
    Task<string?> SelectOptionAsync(string title, IReadOnlyList<string> options, int initial, CancellationToken ct);
}
