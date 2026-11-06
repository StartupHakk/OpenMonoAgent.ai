namespace OpenMono.Llm;

/// <summary>Picks the model id from a /v1/models listing. A server can list several models
/// (Foundry Local keeps every loaded model), so a configured model (OPENMONO_MODEL, llm.model)
/// that the server lists is kept; otherwise the first listed id is used.</summary>
public static class ServedModelPicker
{
    public static string? Pick(string? configured, IReadOnlyList<string> servedIds)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var listed = servedIds.FirstOrDefault(id => string.Equals(id, configured, StringComparison.OrdinalIgnoreCase));
            if (listed is not null)
                return listed;
        }

        return servedIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }
}
