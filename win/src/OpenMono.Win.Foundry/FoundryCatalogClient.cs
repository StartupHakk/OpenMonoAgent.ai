namespace OpenMono.Win.Foundry;

// Lists the models the local Foundry catalog knows about and resolves the
// desired alias against that live list. Catalog output spelling drifts
// between releases, so parsing is best-effort: one candidate per line,
// header and blank lines skipped. Anything unresolved returns null and the
// caller falls back with a clear reason instead of guessing a download.
public sealed class FoundryCatalogClient
{
    private readonly ICliRunner _runner;

    public FoundryCatalogClient(ICliRunner runner)
    {
        _runner = runner;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var (file, args) = FoundryCliCommands.ModelList();
        CliResult result;
        try
        {
            result = await _runner.RunAsync(file, args, ct);
        }
        catch (Exception)
        {
            return [];
        }

        if (!result.Succeeded)
        {
            return [];
        }

        return ParseCatalog(result.StandardOutput);
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

    // Pure so tests can pin the lenient parsing without spawning the CLI.
    public static IReadOnlyList<string> ParseCatalog(string output)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return models;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Headers, prose, and table rows contain whitespace or separators;
            // catalog aliases are single tokens. Skip anything else rather
            // than guessing which column is the alias.
            if (line.Any(char.IsWhiteSpace) || line.Contains('|') || line.Contains(':'))
            {
                continue;
            }

            var token = line.Trim('|', '-', '*');
            if (string.IsNullOrWhiteSpace(token) || !token.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            models.Add(token);
        }

        return models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
