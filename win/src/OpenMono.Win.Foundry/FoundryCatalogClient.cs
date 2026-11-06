using System.Text.Json;

namespace OpenMono.Win.Foundry;

// Lists the models the local Foundry catalog knows about and resolves the
// desired alias against that live list. `model list --output json` is tried
// first; if that flag is unsupported or the shape is unfamiliar, the text
// table is parsed instead. Anything unresolved returns null and the caller
// falls back with a clear reason instead of guessing a download.
public sealed class FoundryCatalogClient
{
    private static readonly string[] PreferredJsonKeys = ["alias"];
    private static readonly string[] SecondaryJsonKeys = ["modelId", "model_id", "id", "name"];

    private readonly ICliRunner _runner;

    public FoundryCatalogClient(ICliRunner runner)
    {
        _runner = runner;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var json = await TryRunAsync(FoundryCliCommands.ModelListJson(), ct);
        if (json is not null)
        {
            var parsed = ParseCatalogJson(json);
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        var text = await TryRunAsync(FoundryCliCommands.ModelList(), ct);
        return text is null ? [] : ParseCatalog(text);
    }

    public async Task<string?> ResolveTargetAsync(
        string desiredAlias,
        string? fallbackAlias = null,
        CancellationToken ct = default)
    {
        var catalog = await ListModelsAsync(ct);
        if (catalog.Count == 0)
        {
            return null;
        }

        return FoundryAliasResolver.Resolve(desiredAlias, catalog)
            ?? (fallbackAlias is not null ? FoundryAliasResolver.Resolve(fallbackAlias, catalog) : null);
    }

    // Accepts any JSON document and collects catalog names from objects in
    // it: "alias" values when present anywhere, otherwise id or name fields.
    public static IReadOnlyList<string> ParseCatalogJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return [];
        }

        using (doc)
        {
            var preferred = new List<string>();
            var secondary = new List<string>();
            Collect(doc.RootElement, preferred, secondary);

            var chosen = preferred.Count > 0 ? preferred : secondary;
            return chosen.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // Text fallback. With an "Alias" header row, the first column of each
    // table row is the alias. Without a header, only single-token lines count
    // so prose never turns into a model name.
    public static IReadOnlyList<string> ParseCatalog(string output)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return models;
        }

        var tableMode = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.All(c => c is '-' or '=' or '|' or '+' or ' '))
            {
                continue;
            }

            var firstToken = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim('|');
            if (string.Equals(firstToken, "alias", StringComparison.OrdinalIgnoreCase))
            {
                tableMode = true;
                continue;
            }

            if (tableMode)
            {
                if (LooksLikeModelName(firstToken))
                {
                    models.Add(firstToken);
                }

                continue;
            }

            if (line.Any(char.IsWhiteSpace) || line.Contains('|') || line.Contains(':'))
            {
                continue;
            }

            var token = line.Trim('|', '-', '*');
            if (LooksLikeModelName(token))
            {
                models.Add(token);
            }
        }

        return models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool LooksLikeModelName(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Any(char.IsLetter)
        && token.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private static void Collect(JsonElement element, List<string> preferred, List<string> secondary)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && LooksLikeModelName(item.GetString() ?? ""))
                    {
                        secondary.Add(item.GetString()!);
                    }
                    else
                    {
                        Collect(item, preferred, secondary);
                    }
                }

                break;

            case JsonValueKind.Object:
                var tookPreferred = TryAdd(element, PreferredJsonKeys, preferred);
                if (!tookPreferred)
                {
                    TryAdd(element, SecondaryJsonKeys, secondary);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        Collect(property.Value, preferred, secondary);
                    }
                }

                break;
        }
    }

    private static bool TryAdd(JsonElement obj, string[] keys, List<string> sink)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (keys.Any(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String
                && LooksLikeModelName(property.Value.GetString() ?? ""))
            {
                sink.Add(property.Value.GetString()!);
                return true;
            }
        }

        return false;
    }

    private async Task<string?> TryRunAsync((string File, string Args) command, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(command.File, command.Args, ct);
            return result.Succeeded ? result.StandardOutput : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
