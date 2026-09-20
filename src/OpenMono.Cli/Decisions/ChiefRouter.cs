namespace OpenMono.Decisions;

public sealed class ChiefRouter(DecisionOptions options, string workingDirectory, IDecisionBackend? backend = null)
{
    public const int MaxStateChars = 200000;

    private static readonly string[] DoneMarkers =
    ["complete", "completed", "done", "finished", "published", "shipped"];

    private static readonly string[] UnclearMarkers =
    ["unclear", "unknown", "tbd", "confused", "vague", "out of scope"];

    private static readonly string[] EmptyMarkers =
    ["no sources", "no evidence", "no findings", "none yet", "not started", "no data"];

    private static readonly string[] EvidenceMarkers =
    ["source", "sources", "evidence", "finding", "findings", "collected",
        "gathered", "researched", "drafted", "interviewed", "measured"];

    public static readonly IReadOnlyDictionary<string, string?> WorkerOptions =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["research"] = "Collect evidence. No sources yet, gather findings first.",
            ["write"] = "Draft from collected evidence, sources and findings.",
            ["review"] = "Goal unclear, out of scope, or work complete.",
        };

    public DecisionOptions Options { get; } = options;

    private readonly IDecisionBackend _backend = backend ?? DecisionBackendFactory.Create(options);

    public async Task<(JobHandoff Handoff, string Path)> RouteAsync(
        string goal, string completedWork, double? autoThreshold, CancellationToken ct)
    {
        var auto = autoThreshold ?? Options.AutoThreshold;
        var state = string.Concat(goal, "\n", completedWork);
        if (state.Length > MaxStateChars)
            return await SaveHandoffAsync(goal, completedWork, "review", "review", 0.9, auto, true, ct);
        var lowered = state.ToLowerInvariant();
        if (ContainsAny(lowered, DoneMarkers) || ContainsAny(lowered, UnclearMarkers))
            return await SaveHandoffAsync(goal, completedWork, "review", "review", 0.9, auto, false, ct);
        if (ContainsAny(lowered, EmptyMarkers) || !ContainsAny(lowered, EvidenceMarkers))
            return await SaveHandoffAsync(goal, completedWork, "research", "research", 0.9, auto, false, ct);
        var backend = _backend;
        var workerMenu = ChoiceMenu.Build("next_worker", WorkerOptions.Select(kv => (kv.Key, kv.Value)).ToList());
        var (pick, confidence, _) = backend.Choose(state, workerMenu);
        var choice = pick == ChoiceMenu.OtherKey ? "review" : pick;
        var gate = DecisionPolicy.ApplyGate(choice, confidence, auto, Options.ReviewThreshold);
        var destination = gate == choice && choice != "review" ? choice : "review";
        return await SaveHandoffAsync(goal, completedWork, choice, destination, confidence, auto, false, ct);
    }

    private async Task<(JobHandoff Handoff, string Path)> SaveHandoffAsync(
        string goal, string work, string choice, string destination,
        double confidence, double auto, bool capped, CancellationToken ct)
    {
        var hash = JobHandoff.ComputeHash(goal, work, destination);
        var existing = JobHandoff.FindByHash(workingDirectory, destination, hash);
        if (existing is not null)
            return (existing, HandoffPath(workingDirectory, existing));
        var handoff = new JobHandoff
        {
            Goal = goal,
            CompletedWork = work,
            Choice = choice,
            Confidence = confidence,
            Destination = destination,
            Status = "queued",
            Model = "local-heuristic",
            AutoThreshold = auto,
            ReviewThreshold = Options.ReviewThreshold,
            Timestamp = DateTime.UtcNow.ToString("o"),
            Uuid = Guid.NewGuid().ToString("N"),
            ContentHash = hash,
            Capped = capped,
        };
        var path = await handoff.SaveAsync(workingDirectory, ct);
        return (handoff, path);
    }

    public static string HandoffPath(string workingDirectory, JobHandoff handoff) =>
        Path.Combine(workingDirectory, ".openmono", "decision-queue", handoff.Destination, handoff.Uuid + ".json");

    private static bool ContainsAny(string lowered, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
