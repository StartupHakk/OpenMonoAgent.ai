namespace OpenMono.Llm;

/// <summary>Picks the model id from a /v1/models listing. Foundry Local lists its whole catalog
/// there, loaded or not, so the first id says nothing about what is served. A configured model
/// (OPENMONO_MODEL, llm.model) is kept; a loaded id (Foundry's /openai/loadedmodels) is preferred
/// when the configured one is unknown to the server. A listing of exactly one model (a
/// single-model llama-server) is that model; otherwise the first listed id is only a last
/// resort when nothing is configured.</summary>
public static class ServedModelPicker
{
    public static string? Pick(string? configured, IReadOnlyList<string> listedIds, IReadOnlyList<string>? loadedIds = null)
    {
        var loaded = (loadedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var known = loaded.Concat(listedIds)
                .FirstOrDefault(id => string.Equals(id, configured, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
                return known;
            if (loaded.Count > 0)
                return loaded[0];
            var listed = listedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            return listed.Count == 1 ? listed[0] : configured;
        }

        return loaded.Count > 0 ? loaded[0] : listedIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }
}
