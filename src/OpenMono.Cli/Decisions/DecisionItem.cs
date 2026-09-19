namespace OpenMono.Decisions;

public sealed record DecisionItem(string Kind, string Id, string Action, double Probability, int Index, string? Detail)
{
    public string CanonicalRef => string.Concat(Kind, "\0", Id);
}
