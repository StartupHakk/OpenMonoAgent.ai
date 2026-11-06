namespace OpenMono.Win.Foundry;

// Adopt a healthy server the user already runs; bind a new one only when the
// loopback port has nothing usable. Binding twice is the failure to avoid.
public enum EndpointDecision
{
    AdoptExisting,
    StartNew,
}

public sealed record EndpointPlan(EndpointDecision Decision, string Endpoint, string Reason);

public static class FoundryEndpointDecider
{
    public static EndpointPlan Decide(
        FoundryHealth existing,
        string desiredEndpoint,
        string expectedModel)
    {
        if (existing.Reachable)
        {
            if (existing.ListsModel(expectedModel))
            {
                return new EndpointPlan(
                    EndpointDecision.AdoptExisting,
                    desiredEndpoint,
                    $"Server already lists {expectedModel}; reusing it instead of binding twice.");
            }

            return new EndpointPlan(
                EndpointDecision.AdoptExisting,
                desiredEndpoint,
                "Server is reachable but does not list the expected model; reusing it "
                    + "and leaving model load to the host instead of binding a second server.");
        }

        return new EndpointPlan(
            EndpointDecision.StartNew,
            desiredEndpoint,
            $"Nothing healthy on {desiredEndpoint} ({existing.Error ?? "no detail"}); starting a new server.");
    }
}
