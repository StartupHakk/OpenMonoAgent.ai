using OpenMono.Config;

namespace OpenMono.Decisions;

public sealed record DecisionOptions(bool Enabled, double AutoThreshold, double ReviewThreshold, double MinConfidence, int MaxSteps)
{
    public IReadOnlyList<string> ForceAskPatterns { get; init; } = [];
    public static DecisionOptions FromSettings(DecisionSettings settings) => new(
        settings.Enabled,
        Math.Clamp(settings.AutoThreshold, 0, 1),
        Math.Clamp(settings.ReviewThreshold, 0, 1),
        Math.Clamp(settings.MinConfidence, 0, 1),
        Math.Clamp(settings.MaxSteps, 1, 256))
    {
        ForceAskPatterns = [.. settings.ForceAskPatterns.Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 200).Take(32)]
    };
}
