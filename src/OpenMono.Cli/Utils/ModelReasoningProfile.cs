namespace OpenMono.Utils;

public enum ReasoningKind
{
    None,
    BinaryToggle,
    EffortLevels,
}

public sealed record ServerReasoningInfo
{
    public string? ReasoningFormat { get; init; }
    public bool ReasoningInContent { get; init; }
    public bool HasThinkingTemplate { get; init; }
    public string[]? EffortLevels { get; init; }
    public string? EffortDefaultLevel { get; init; }

    public static bool TemplateShowsThinking(string? chatTemplate)
    {
        if (string.IsNullOrEmpty(chatTemplate))
            return false;
        return chatTemplate.Contains("enable_thinking", StringComparison.Ordinal)
            || chatTemplate.Contains("<think>", StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex EffortTuplePattern = new(
        @"reasoning_effort\s+not\s+in\s*\(([^)]*)\)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

    private static readonly System.Text.RegularExpressions.Regex EffortDefaultPattern = new(
        @"reasoning_effort\s*\|\s*default\s*\(\s*['""]([^'""]+)['""]\s*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex QuotedTokenPattern = new(
        @"['""]([^'""]+)['""]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static (string[] Levels, string? DefaultLevel) ParseEffortLevels(string? chatTemplate)
    {
        if (string.IsNullOrEmpty(chatTemplate))
            return ([], null);

        var levels = new List<string>();
        var tuple = EffortTuplePattern.Match(chatTemplate);
        if (tuple.Success)
        {
            foreach (System.Text.RegularExpressions.Match m in QuotedTokenPattern.Matches(tuple.Groups[1].Value))
            {
                var token = m.Groups[1].Value.Trim();
                if (token.Length > 0 && !levels.Contains(token, StringComparer.OrdinalIgnoreCase))
                    levels.Add(token);
            }
        }

        string? def = null;
        var dm = EffortDefaultPattern.Match(chatTemplate);
        if (dm.Success)
        {
            var candidate = dm.Groups[1].Value.Trim();
            if (candidate.Length > 0)
                def = candidate;
        }

        return (levels.ToArray(), def);
    }
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
        => Resolve(modelName, serverInfo: null);

    public static ModelReasoningProfile Resolve(string? modelName, ServerReasoningInfo? serverInfo)
    {
        if (serverInfo?.EffortLevels is { Length: > 0 } advertised)
            return FromAdvertisedLevels(modelName, advertised, serverInfo.EffortDefaultLevel);

        if (serverInfo is not null)
        {
            if (serverInfo.HasThinkingTemplate || serverInfo.ReasoningInContent)
                return ForKnownOrGeneric(modelName);
            if (serverInfo.ReasoningFormat is string format && !format.Equals("none", StringComparison.OrdinalIgnoreCase))
                return ForKnownOrGeneric(modelName);
            if (serverInfo.ReasoningFormat is not null)
                return _none;
        }

        return ForKnownOrGeneric(modelName, unknownMeansNone: true);
    }

    private static readonly Dictionary<string, int> EffortRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = 0,
        ["medium"] = 1,
        ["high"] = 2,
        ["xhigh"] = 3,
    };

    private static ModelReasoningProfile FromAdvertisedLevels(string? modelName, string[] advertised, string? advertisedDefault)
    {
        var ordered = advertised
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => EffortRank.TryGetValue(l, out var r) ? r : 99)
            .ToArray();
        var known = MapByName(modelName);
        var fallbackDefault = known?.DefaultLevel
            ?? (advertisedDefault is string d && ordered.Contains(d, StringComparer.OrdinalIgnoreCase)
                ? d
                : (ordered.Contains("low", StringComparer.OrdinalIgnoreCase) ? "low" : ordered[0]));
        return new ModelReasoningProfile
        {
            Kind = ReasoningKind.EffortLevels,
            DefaultEnabled = true,
            DefaultLevel = fallbackDefault,
            Levels = ["off", .. ordered],
            ThinkingTemperature = known?.ThinkingTemperature,
            ThinkingTopP = known?.ThinkingTopP,
            PreserveThinking = known?.PreserveThinking ?? false,
        };
    }

    private static ModelReasoningProfile? MapByName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        var lower = modelName.ToLowerInvariant();

        if (lower.Contains("qwen-3.8") || lower.Contains("qwen3.8") || lower.StartsWith("qwen38-"))
            return _qwen38;
        if (lower.StartsWith("qwen"))
            return _qwen3;

        return null;
    }

    private static ModelReasoningProfile ForKnownOrGeneric(string? modelName, bool unknownMeansNone = false)
        => MapByName(modelName) ?? (unknownMeansNone ? _none : BinaryFallback);

    private static readonly ModelReasoningProfile BinaryFallback = new()
    {
        Kind = ReasoningKind.BinaryToggle,
        DefaultEnabled = false,
        DefaultLevel = "off",
    };
}
