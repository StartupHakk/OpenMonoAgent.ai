namespace OpenMono.Playbooks;

/// <summary>
/// Represents the pre-computed plan of all steps and tools a playbook may use.
/// Used to display to the user for batch approval before execution starts.
/// </summary>
public sealed record PlaybookToolPlan
{
    public required string PlaybookName { get; init; }
    public required IReadOnlyList<PlaybookPlanStep> Steps { get; init; }
    public required IReadOnlyList<PlaybookPlanTool> Tools { get; init; }
    public bool RequiresModeSwitch { get; init; }
}

/// <summary>
/// A step in the playbook plan (for user review).
/// </summary>
public sealed record PlaybookPlanStep
{
    public required string Id { get; init; }
    public required GateType Gate { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// A tool that the playbook may use (for user review).
/// </summary>
public sealed record PlaybookPlanTool
{
    public required string Name { get; init; }
    public required bool IsReadOnly { get; init; }
    public bool Dangerous { get; init; }
}
