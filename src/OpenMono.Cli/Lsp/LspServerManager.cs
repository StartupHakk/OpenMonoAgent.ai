namespace OpenMono.Lsp;

public sealed record LspServerConfig
{
    public required string Language { get; init; }
    public required string Command { get; init; }
    public string[]? Args { get; init; }
}

public sealed class LspServerManager : IDisposable
{
    private readonly Dictionary<string, LspClient> _clients = [];
    private readonly Dictionary<string, LspServerConfig> _configs = [];
    private readonly string _workspaceRoot;
    private readonly Action<string>? _warn;

    public static readonly LspServerConfig[] DefaultServers =
    [
        new() { Language = "csharp", Command = "omnisharp", Args = ["-lsp"] },
        new() { Language = "typescript", Command = "typescript-language-server", Args = ["--stdio"] },
        new() { Language = "python", Command = "pylsp" },
        new() { Language = "go", Command = "gopls", Args = ["serve"] },
        new() { Language = "rust", Command = "rust-analyzer" },
    ];

    public LspServerManager(string workspaceRoot, Action<string>? warn = null)
    {
        _workspaceRoot = workspaceRoot;
        _warn = warn;

        foreach (var config in DefaultServers)
            _configs[config.Language] = config;
    }

    public void Configure(IEnumerable<LspServerConfig> configs)
    {
        foreach (var config in configs)
            _configs[config.Language] = config;
    }

    public async Task<LspClient?> GetClientAsync(string filePath, CancellationToken ct)
    {
        var language = DetectLanguage(filePath);
        if (language is null) return null;

        if (_clients.TryGetValue(language, out var existing) && existing.IsRunning)
            return existing;

        if (!_configs.TryGetValue(language, out var config))
            return null;

        try
        {
            var client = await LspClient.StartAsync(config, _workspaceRoot, ct);
            _clients[language] = client;
            _warn?.Invoke($"LSP: Started {config.Command} for {language}");
            return client;
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"LSP: Failed to start {config.Command}: {ex.Message}");
            return null;
        }
    }

    private static string? DetectLanguage(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            _ => null,
        };

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
