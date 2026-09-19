using OpenMono.Playbooks;
using OpenMono.Session;

namespace OpenMono.Commands;

/// <summary>
/// REPL twin of the ACP <c>/playbook</c> command: runs a playbook deterministically
/// via <see cref="ConversationLoop.ExecutePlaybookDirectAsync"/> instead of asking
/// the agent to invoke it.
/// </summary>
public sealed class PlaybookCommand : ICommand
{
    private readonly ConversationLoop _loop;
    private readonly PlaybookRegistry? _registry;

    public PlaybookCommand(ConversationLoop loop, PlaybookRegistry? registry = null)
    {
        _loop = loop;
        _registry = registry;
    }

    public string Name => "playbook";
    public string Description => "Run a playbook directly: /playbook <name> [--resume] [--key=value...]";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var tail = string.Join(" ", args).Trim();
        var (name, playbookArgs, resume) = PlaybookArgParser.SplitCommandTail(tail);
        if (string.IsNullOrEmpty(name))
        {
            var available = _registry is { All.Count: > 0 }
                ? string.Join(", ", _registry.All.Select(p => p.Name))
                : "(no playbooks registered)";
            context.Renderer.WriteWarning($"Usage: /playbook <name> [--resume] [--key=value...]\nAvailable: {available}");
            return;
        }

        if (_registry?.Resolve(name) is null && _registry is not null)
        {
            context.Renderer.WriteWarning(
                $"Playbook '{name}' not found. Available: {string.Join(", ", _registry.All.Select(p => p.Name))}");
            return;
        }

        var result = await _loop.ExecutePlaybookDirectAsync(name, playbookArgs, resume, ct);
        if (result.IsError)
            context.Renderer.WriteWarning($"Playbook '{name}' failed: {result.ContentForModel}");
        else
            context.Renderer.WriteMarkdown(result.ContentForModel);
    }
}
