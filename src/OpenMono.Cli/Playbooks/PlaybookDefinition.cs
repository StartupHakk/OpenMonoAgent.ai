namespace OpenMono.Playbooks;

public sealed record PlaybookDefinition
{
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public required string Description { get; init; }

    public TriggerMode Trigger { get; init; } = TriggerMode.Manual;
    public string[] TriggerPatterns { get; init; } = [];
    public bool UserInvocable { get; init; } = true;
    /// <summary>Either "global" (~/.openmono/playbooks/) or "workspace" (.openmono/playbooks/).</summary>
    public string? Scope { get; init; }
    public string? ArgumentHint { get; init; }

    public Dictionary<string, ParameterDefinition> Parameters { get; init; } = [];

    public StepDefinition[] Steps { get; init; } = [];
    public ConstraintSet Constraints { get; init; } = new();

    /// <summary>Tools this playbook is allowed to use. Empty array means no tools allowed (deny by default).
    /// Use specific tool names or patterns like "Tool*" to allow tools. </summary>
    public string[] AllowedTools { get; init; } = [];
    public ContextMode ContextMode { get; init; } = ContextMode.Selective;
    public int MaxContextTokens { get; init; } = 3000;

    public string[] DependsOn { get; init; } = [];

    public string[] Tags { get; init; } = [];
    public string BasePath { get; init; } = "";
    public string? RoleDescription { get; init; }

    public bool SkipPermissions { get; init; } = false;

    public bool LogOutput { get; init; } = false;

    /// <summary>When true, the executor records per-call/per-step context usage and appends a per-run
    /// object to <c>.openmono/data/ctx-usage.jsonl</c> (JSONL). Default off: no capture, no file.</summary>
    public bool ReportCtx { get; init; } = false;

    /// <summary>Maximum tool-call rounds per step before aborting. Default: 10.</summary>
    public int MaxToolLoops { get; init; } = 10;

    /// <summary>When set, overrides the global config temperature for this playbook's LLM calls.</summary>
    public double? Temperature { get; init; }

    /// <summary>Reasoning level for this playbook's LLM calls. Values: "off", "low", "medium",
    /// "xhigh" (valid set is model-dependent; an unknown value warns and falls back to the model's
    /// default level). Default "off", matching the interactive session default. Ignored, with a
    /// warning, on models without reasoning support.</summary>
    public string? Thinking { get; init; }
}

public sealed record ParameterDefinition
{
    public required ParameterType Type { get; init; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    public string? Description { get; init; }
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
    public string? OutputSchema { get; init; }
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
