namespace OpenMono.Utils;

public enum ReasoningKind
{
    None,
    BinaryToggle,
    EffortLevels,
}

public sealed record ModelReasoningProfile
{
    public ReasoningKind Kind { get; init; }
    public bool DefaultEnabled { get; init; }
    public string DefaultLevel { get; init; } = "off";
    public string[] Levels { get; init; } = [];
    public double? ThinkingTemperature { get; init; }
    public double? ThinkingTopP { get; init; }
    public bool PreserveThinking { get; init; }

    private static readonly ModelReasoningProfile _none = new() { Kind = ReasoningKind.None };

    private static readonly ModelReasoningProfile _qwen3 = new()
    {
        Kind = ReasoningKind.BinaryToggle,
        DefaultEnabled = false,
        DefaultLevel = "off",
        ThinkingTemperature = 0.6,
        ThinkingTopP = 0.95,
    };

    private static readonly ModelReasoningProfile _qwen38 = new()
    {
        Kind = ReasoningKind.EffortLevels,
        DefaultEnabled = true,
        DefaultLevel = "low",
        Levels = ["off", "low", "medium", "xhigh"],
        ThinkingTemperature = 1.0,
        ThinkingTopP = 0.95,
        PreserveThinking = true,
    };

    public static ModelReasoningProfile Resolve(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return _none;

        var lower = modelName.ToLowerInvariant();

        if (lower.Contains("qwen-3.8") || lower.Contains("qwen3.8") || lower.StartsWith("qwen38-"))
            return _qwen38;
        if (lower.StartsWith("qwen"))
            return _qwen3;

        return _none;
    }
}
