using OpenMono.Utils;

namespace OpenMono.Decisions;

/// <summary>
/// Creates the active <see cref="IDecisionBackend"/> from
/// <see cref="DecisionOptions.Backend"/>. Only <c>"heuristic"</c> is wired;
/// any other value (including a future <c>"systemone"</c> before Phase 5
/// lands) falls back to heuristic with a warning so the gate never fails
/// closed on a typo.
/// </summary>
public static class DecisionBackendFactory
{
    public const string HeuristicName = "heuristic";

    public static IDecisionBackend Create(DecisionOptions options)
    {
        var name = (options.Backend ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name == HeuristicName)
            return new HeuristicBackend(options);
        Log.Warn($"Unknown decision backend '{options.Backend}', falling back to heuristic.");
        return new HeuristicBackend(options);
    }

    public static string Normalize(string? name)
    {
        var cleaned = (name ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(cleaned) ? HeuristicName : cleaned;
    }
}
