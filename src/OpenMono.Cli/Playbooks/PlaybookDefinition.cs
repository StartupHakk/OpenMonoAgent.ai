namespace OpenMono.Playbooks;

public sealed record PlaybookDefinition
{
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public required string Description { get; init; }

    public TriggerMode Trigger { get; init; } = TriggerMode.Manual;
    public string[] TriggerPatterns { get; init; } = [];
    public bool UserInvocable { get; init; } = true;
    public string? ArgumentHint { get; init; }

    public Dictionary<string, ParameterDefinition> Parameters { get; init; } = [];

    public StepDefinition[] Steps { get; init; } = [];
    public ConstraintSet Constraints { get; init; } = new();

    public string[] AllowedTools { get; init; } = ["*"];
    public ContextMode ContextMode { get; init; } = ContextMode.Selective;
    public int MaxContextTokens { get; init; } = 3000;

    public string[] DependsOn { get; init; } = [];

    public string[] Tags { get; init; } = [];
    public string BasePath { get; init; } = "";
    public string? RoleDescription { get; init; }
}

public sealed record ParameterDefinition
{
    public required ParameterType Type { get; init; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    public string? Hint { get; init; }
    public string[]? Enum { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
}

public sealed record StepDefinition
{
    public required string Id { get; init; }
    public string? File { get; init; }
    public string? InlinePrompt { get; init; }
    public string[] Requires { get; init; } = [];
    public GateType Gate { get; init; } = GateType.None;
    public string? Agent { get; init; }
    public string? Output { get; init; }
    public string? Script { get; init; }
    public string? Playbook { get; init; }
    public Dictionary<string, string>? Params { get; init; }
}

public sealed record ConstraintSet
{
    public string? File { get; init; }
    public List<string> Inline { get; init; } = [];
}

public enum TriggerMode { Manual, Auto, Both }
public enum GateType { None, Confirm, Review, Approve }
public enum ContextMode { Full, Selective, Fork }
public enum ParameterType { String, Number, Boolean, Array }
