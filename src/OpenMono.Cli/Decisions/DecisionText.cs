namespace OpenMono.Decisions;

/// <summary>
/// Token-boundary text checks shared by the heuristic backend and routers
/// (Phase 2 brittleness fix). Substring <c>Contains</c> checks false-fire:
/// "knot" contains "not ", "noted" starts with "not", and "terror"
/// contains "error". Every check here operates on whole-word tokens or on
/// whitespace-normalized text with boundary padding, so markers only match
/// at token boundaries.
/// </summary>
internal static class DecisionText
{
    private static readonly HashSet<string> NegationTokens = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "none", "cannot",
        "without", "neither", "nor", "nobody", "nothing", "nowhere",
    };

    /// <summary>
    /// Raw lowercase word tokens with no stopword filtering and no minimum
    /// length, so short markers ("no", "not") and multiword phrases stay
    /// detectable. Contractions split ("don't" → "don"+"t") and are covered
    /// by the explicit n't check in <see cref="ContainsNegation"/>.
    /// </summary>
    public static List<string> WordTokens(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                current.Append(ch);
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Token-boundary negation: a standalone negation word, or an n't
    /// contraction (the apostrophe makes the substring check safe —
    /// "knot"/"noted" contain no apostrophe).
    /// </summary>
    public static bool ContainsNegation(string text)
    {
        if (text.Contains("n't", StringComparison.OrdinalIgnoreCase))
            return true;
        return WordTokens(text).Any(NegationTokens.Contains);
    }

    /// <summary>
    /// Token-boundary phrase match: both haystack and phrase are normalized
    /// (lowercased, non-alphanumeric → single spaces, boundary-padded).
    /// Single-word markers match any whole-word occurrence ("done" matches
    /// "I'm done." but not "undone"); multiword markers must appear as an
    /// adjacent token run ("no sources", "out of scope").
    /// </summary>
    public static bool ContainsPhrase(string text, string phrase)
    {
        var padded = NormalizePadded(text);
        var needle = NormalizePadded(phrase).Trim();
        return needle.Length > 0 && padded.Contains($" {needle} ", StringComparison.Ordinal);
    }

    public static bool ContainsAnyPhrase(string text, IEnumerable<string> markers)
    {
        var padded = NormalizePadded(text);
        foreach (var marker in markers)
        {
            var needle = NormalizePadded(marker).Trim();
            if (needle.Length > 0 && padded.Contains($" {needle} ", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Coarse non-English signal: four or more non-ASCII letters. Callers
    /// must treat this as an abstain path (route to review), never as a
    /// silent misroute — token-overlap scoring is English-shaped and its
    /// output on other languages is not signal.
    /// </summary>
    public static bool LooksNonEnglish(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch > 127 && char.IsLetter(ch) && ++count >= 4)
                return true;
        }
        return false;
    }

    private static string NormalizePadded(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 2);
        sb.Append(' ');
        var pendingSpace = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(ch);
            }
            else if (sb.Length > 1 && !pendingSpace)
            {
                pendingSpace = true;
            }
        }
        sb.Append(' ');
        return sb.ToString();
    }
}
