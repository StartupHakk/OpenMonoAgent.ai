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

    // Inkling 2 Mini (Strata SQ packs). The chat template rejects "high"
    // (the bench saw that on the same family of templates). 0.2.22 accepts
    // low / medium / xhigh and defaults to low when the request says nothing.
    // The RTX 3060 profile also sets llm.reasoning_effort so the request
    // carries "low" instead of the "high" / "none" values those templates drop.
    private static readonly ModelReasoningProfile _inkling = new()
    {
        Kind = ReasoningKind.EffortLevels,
        DefaultEnabled = true,
        DefaultLevel = "low",
        Levels = ["off", "low", "medium", "xhigh"],
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
            .Where(l => !IsInkling(modelName) || !l.Equals("high", StringComparison.OrdinalIgnoreCase))
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

        if (lower.Contains("inkling"))
            return _inkling;
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

    public static bool IsInkling(string? modelName) =>
        !string.IsNullOrWhiteSpace(modelName)
        && modelName.Contains("inkling", StringComparison.OrdinalIgnoreCase);

    public static bool IsActiveEffort(string? effort) =>
        !string.IsNullOrWhiteSpace(effort)
        && !effort.Equals("off", StringComparison.OrdinalIgnoreCase)
        && !effort.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Effort sent as the top-level <c>reasoning_effort</c> field.
    /// Inkling templates reject "high", so that value becomes "low".
    /// Other models keep the existing Strata clamp: "xhigh" becomes "high".
    /// </summary>
    public static string NormalizeTopLevelEffort(string? modelName, string effort)
    {
        if (IsInkling(modelName))
        {
            if (effort.Equals("high", StringComparison.OrdinalIgnoreCase))
                return "low";
            return effort;
        }
        if (effort.Equals("xhigh", StringComparison.OrdinalIgnoreCase))
            return "high";
        return effort;
    }

    /// <summary>
    /// Effort sent inside <c>chat_template_kwargs</c>. Qwen keeps "xhigh" here
    /// (llama.cpp understands it). Inkling still must not be sent "high".
    /// </summary>
    public static string NormalizeTemplateEffort(string? modelName, string effort)
    {
        if (IsInkling(modelName) && effort.Equals("high", StringComparison.OrdinalIgnoreCase))
            return "low";
        return effort;
    }

    /// <summary>
    /// Which effort to put on the request. A configured <c>llm.reasoning_effort</c>
    /// wins over the session or playbook level. When it is absent, the previous
    /// rule stands: only effort-level models with thinking on send a level.
    /// </summary>
    public static string? SelectRequestEffort(string? configured, string? level, bool thinking, ReasoningKind kind)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var chosen = configured.Trim();
            if (chosen.Equals("off", StringComparison.OrdinalIgnoreCase))
                return null;
            return chosen;
        }
        if (kind == ReasoningKind.EffortLevels && thinking && IsActiveEffort(level))
            return level;
        return null;
    }

    public static (bool Enabled, string Level) SessionThinking(ModelReasoningProfile profile, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var level = configured.Trim();
            var enabled = IsActiveEffort(level);
            return (enabled, enabled ? level : "off");
        }
        var on = profile.DefaultEnabled && profile.DefaultLevel != "off";
        return (on, profile.DefaultLevel);
    }
}
