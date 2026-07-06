using OpenMono.Commands;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;

namespace OpenMono.Acp;
















public sealed class AcpInputReaderAdapter : IInputReader
{
    private readonly IAcpUserInteraction _interaction;

    public AcpInputReaderAdapter(IAcpUserInteraction interaction)
    {
        _interaction = interaction;
    }

    public async Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
    {
        var dangerous = LooksDestructive(toolName, summary);
        var (allow, scope) = await _interaction.RequestPermissionAsync(toolName, summary, dangerous, ct);
        // Map (allow, scope) back to PermissionResponse enum
        return (allow, scope) switch
        {
            (true, "session") => PermissionResponse.AllowAll,
            (true, _) => PermissionResponse.Allow,
            (false, "session") => PermissionResponse.DenyAll,
            _ => PermissionResponse.Deny
        };
    }

    public async Task<string> AskUserAsync(string question, CancellationToken ct)
    {
        var value = await _interaction.RequestUserInputAsync(question, ct);
        return value ?? string.Empty;
    }

    public async Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct)
    {
        return await _interaction.RequestPlaybookApprovalAsync(plan, ct);
    }

    public void EnableCommandSuggestions(CommandRegistry registry) { }
    public string ReadInput() => string.Empty;
    public string? ShowCommandPicker(CommandRegistry registry) => null;







    private static bool LooksDestructive(string toolName, string summary)
    {
        if (string.Equals(toolName, "Bash", StringComparison.Ordinal))
        {
            var lower = summary.ToLowerInvariant();
            if (lower.Contains("rm -rf") || lower.Contains("rm -fr")) return true;
            if (lower.Contains("git reset --hard")) return true;
            if (lower.Contains("git push --force") || lower.Contains("git push -f")) return true;
            if (lower.Contains("docker volume rm") || lower.Contains("docker system prune")) return true;
            if (lower.Contains("mkfs") || lower.Contains("dd if=")) return true;
        }
        if (string.Equals(toolName, "FileWrite", StringComparison.Ordinal)) return true;
        if (string.Equals(toolName, "ApplyPatch", StringComparison.Ordinal)) return true;
        return false;
    }
}
