using OpenMono.Playbooks;
using OpenMono.Rendering;

namespace OpenMono.Permissions;

public abstract record Capability
{
    public abstract string Summary { get; }

    public virtual async Task<bool> PromptUserAsync(IInputReader input, string toolName, CancellationToken ct)
    {
        var response = await input.AskPermissionAsync(toolName, Summary, ct);
        return response == PermissionResponse.Allow;
    }
}

public sealed record FileReadCap(string Path) : Capability
{
    public override string Summary => $"Read file: {Path}";
}

public sealed record FileWriteCap(string Path, string Operation = "modify") : Capability
{
    public override string Summary => Operation switch
    {
        "create" => $"Create file: {Path}",
        "delete" => $"Delete file: {Path}",
        _ => $"Modify file: {Path}"
    };
}

public sealed record ProcessExecCap(string Binary, IReadOnlyList<string> Args) : Capability
{
    public override string Summary => Args.Count > 0
        ? $"Execute: {Binary} {string.Join(' ', Args)}"
        : $"Execute: {Binary}";

    public static ProcessExecCap FromCommand(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var binary = parts.Length > 0 ? parts[0] : command;
        var args = parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        return new ProcessExecCap(binary, args);
    }
}

public sealed record NetworkEgressCap(string Host, int Port = 0, string Protocol = "https") : Capability
{
    public override string Summary => Port > 0
        ? $"Network: {Protocol}://{Host}:{Port}"
        : $"Network: {Protocol}://{Host}";

    public static NetworkEgressCap FromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new NetworkEgressCap(uri.Host, uri.Port, uri.Scheme);
        return new NetworkEgressCap(url);
    }
}

public sealed record VcsMutationCap(string Repo, string Operation) : Capability
{
    public override string Summary => $"VCS {Operation}: {Repo}";
}

public sealed record MemoryCap(string Namespace, string Operation) : Capability
{
    public override string Summary => $"Memory {Operation}: {Namespace}";
}

public sealed record AgentSpawnCap(string AgentType, string TaskSummary) : Capability
{
    public override string Summary => $"Spawn agent ({AgentType}): {TaskSummary[..Math.Min(50, TaskSummary.Length)]}...";
}

// Stores gate info per step for reconstruction
public sealed record PlaybookStepInfo(string Id, GateType Gate, string? Description);
public sealed record PlaybookToolInfo(string Name, bool IsReadOnly, bool Dangerous);

public sealed record PlaybookApproveCap(
    string PlaybookName,
    IReadOnlyList<PlaybookStepInfo> Steps,
    IReadOnlyList<PlaybookToolInfo> Tools) : Capability
{
    public override string Summary => $"Run playbook: {PlaybookName} ({Steps.Count} steps)";

    public override async Task<bool> PromptUserAsync(IInputReader input, string toolName, CancellationToken ct)
    {
        var requiresModeSwitch = Tools.Any(t => !t.IsReadOnly);
        var plan = new PlaybookToolPlan
        {
            PlaybookName = PlaybookName,
            Steps = Steps.Select(s => new PlaybookPlanStep { Id = s.Id, Gate = s.Gate, Description = s.Description }).ToList(),
            Tools = Tools.Select(t => new PlaybookPlanTool { Name = t.Name, IsReadOnly = t.IsReadOnly, Dangerous = t.Dangerous }).ToList(),
            RequiresModeSwitch = requiresModeSwitch
        };
        return await input.RequestPlaybookApprovalAsync(plan, ct);
    }
}
