namespace OpenMono.Decisions;

public sealed class HeuristicBackend(DecisionOptions options) : IDecisionBackend
{
    public const double ExactMatchConfidence = 0.95;
    public const double ContainsMatchConfidence = 0.75;
    public const double AlreadySatisfiedConfidence = 0.99;
    public const double SupportedConfidence = 0.9;
    public const double ContradictedConfidence = 0.75;
    public const double NotAddressedConfidence = 0.85;

    public DecisionOptions Options { get; } = options;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with",
        "is", "are", "was", "were", "be", "been", "it", "this", "that",
        "as", "at", "by", "from", "into", "no", "not", "but", "if", "then",
        "so", "than", "too", "very", "can", "will", "just", "should", "now",
    };

    private static readonly string[] Negations =
    ["not ", "no ", "never ", "n't ", "none ", "cannot ", "without "];

    public (string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) Choose(
        string state,
        IReadOnlyDictionary<string, string?> options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var first = options.Keys.First();
        var stateTokens = Tokenize(state);
        if (stateTokens.Count == 0 || options.Count == 0)
            return (first, 1.0 / Math.Max(options.Count, 1), Uniform(options));
        var scores = options.ToDictionary(
            kv => kv.Key,
            kv => ScoreOption(state.ToLowerInvariant(), stateTokens, kv.Key, kv.Value ?? string.Empty));
        var total = scores.Values.Sum();
        if (total <= 0)
            return (first, 1.0 / options.Count, Uniform(options));
        var probs = scores.ToDictionary(kv => kv.Key, kv => kv.Value / total);
        var best = probs.MaxBy(kv => kv.Value).Key;
        return (best, probs[best], probs);
    }

    public double JudgeTrue(string state, string proposition, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stateTokens = new HashSet<string>(Tokenize(state));
        var propTokens = Tokenize(proposition);
        if (propTokens.Count == 0)
            return 0.5;
        var covered = propTokens.Count(stateTokens.Contains);
        var p = (double)covered / propTokens.Count;
        p = Math.Clamp(p, 0.05, 0.95);
        var lowered = proposition.ToLowerInvariant();
        if (Negations.Any(n => lowered.Contains(n, StringComparison.Ordinal)))
            p = 1 - p;
        return p;
    }

    public double Relevance(string query, string candidate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var q = query.Trim().ToLowerInvariant();
        var c = candidate.Trim().ToLowerInvariant();
        if (q.Length == 0 || c.Length == 0)
            return 0;
        if (q.Equals(c, StringComparison.Ordinal))
            return 1.0;
        var f1 = OverlapF1(Tokenize(query), Tokenize(candidate));
        if (c.Contains(q, StringComparison.Ordinal) || q.Contains(c, StringComparison.Ordinal))
            return Math.Max(ContainsMatchConfidence, f1);
        return f1;
    }

    public (string Verdict, double Confidence) Verify(string evidence, string claim, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var evidenceTokens = new HashSet<string>(Tokenize(evidence));
        var claimTokens = Tokenize(claim);
        if (claimTokens.Count == 0)
            return ("not_addressed", NotAddressedConfidence);
        var coverage = (double)claimTokens.Count(evidenceTokens.Contains) / claimTokens.Count;
        if (coverage >= 0.6)
            return ("supported", SupportedConfidence);
        var lowered = evidence.ToLowerInvariant();
        if (coverage >= 0.4 && Negations.Any(n => lowered.Contains(n, StringComparison.Ordinal)))
            return ("contradicted", ContradictedConfidence);
        return ("not_addressed", NotAddressedConfidence);
    }

    private static double ScoreOption(string loweredState, List<string> stateTokens, string key, string description)
    {
        var stateSet = new HashSet<string>(stateTokens);
        var keyTokens = Tokenize(key);
        var descTokens = Tokenize(description);
        var score = 2.0 * keyTokens.Count(stateSet.Contains) + descTokens.Count(stateSet.Contains);
        if (key.Length > 1 && loweredState.Contains(key.ToLowerInvariant(), StringComparison.Ordinal))
            score += 3;
        return score;
    }

    private static double OverlapF1(List<string> left, List<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;
        var rightSet = new HashSet<string>(right);
        var hits = left.Count(rightSet.Contains);
        var precision = (double)hits / left.Count;
        var recall = (double)hits / right.Count;
        return precision + recall <= 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static IReadOnlyDictionary<string, double> Uniform(IReadOnlyDictionary<string, string?> options)
    {
        var p = 1.0 / Math.Max(options.Count, 1);
        return options.ToDictionary(kv => kv.Key, _ => p);
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                AddToken(tokens, current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            AddToken(tokens, current.ToString());
        return tokens;
    }

    private static void AddToken(List<string> tokens, string token)
    {
        if (token.Length >= 2 && !Stopwords.Contains(token))
            tokens.Add(token);
    }
}
