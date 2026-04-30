namespace OpenMono.Playbooks;

public sealed class PlaybookRegistry
{
    private readonly Dictionary<string, PlaybookDefinition> _playbooks = new(StringComparer.OrdinalIgnoreCase);

    public void Register(PlaybookDefinition playbook)
    {
        _playbooks[playbook.Name] = playbook;
    }

    public void RegisterAll(IEnumerable<PlaybookDefinition> playbooks)
    {
        foreach (var pb in playbooks) Register(pb);
    }

    public PlaybookDefinition? Resolve(string name) =>
        _playbooks.GetValueOrDefault(name);

    public IReadOnlyCollection<PlaybookDefinition> All => _playbooks.Values;

    public PlaybookDefinition? MatchTrigger(string userInput)
    {
        PlaybookDefinition? bestMatch = null;
        var bestScore = 0;

        foreach (var pb in _playbooks.Values)
        {
            if (pb.Trigger == TriggerMode.Manual) continue;

            foreach (var pattern in pb.TriggerPatterns)
            {
                var score = MatchPattern(userInput, pattern);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = pb;
                }
            }
        }

        return bestScore > 0 ? bestMatch : null;
    }

    private static int MatchPattern(string input, string pattern)
    {
        var normalized = input.Trim().ToLowerInvariant();
        var patternLower = pattern.Trim().ToLowerInvariant();

        var parts = patternLower.Split('*', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0;

        var pos = 0;
        foreach (var part in parts)
        {
            var idx = normalized.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
            pos = idx + part.Length;
        }

        return parts.Sum(p => p.Length);
    }
}
