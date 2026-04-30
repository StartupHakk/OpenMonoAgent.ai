using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenMono.Lsp;

public sealed class LspClient : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly Stream _stdout;
    private int _requestId;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Language { get; }
    public bool IsRunning => !_process.HasExited;

    private LspClient(Process process, string language)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput.BaseStream;
        Language = language;
    }

    public static async Task<LspClient> StartAsync(LspServerConfig config, string workspaceRoot, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in config.Args ?? []) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start LSP server: {config.Command}");

        var client = new LspClient(process, config.Language);

        await client.SendRequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = $"file://{workspaceRoot}",
            capabilities = new
            {
                textDocument = new
                {
                    hover = new { contentFormat = new[] { "plaintext" } },
                    definition = new { linkSupport = false },
                    references = new { },
                    publishDiagnostics = new { },
                }
            }
        }, ct);

        await client.SendNotificationAsync("initialized", new { }, ct);
        return client;
    }

    public async Task<string?> HoverAsync(string filePath, int line, int character, CancellationToken ct)
    {
        await NotifyDidOpenAsync(filePath, ct);

        var result = await SendRequestAsync("textDocument/hover", new
        {
            textDocument = new { uri = $"file://{filePath}" },
            position = new { line, character },
        }, ct);

        if (result.ValueKind == JsonValueKind.Null) return null;

        if (result.TryGetProperty("contents", out var contents))
        {
            if (contents.ValueKind == JsonValueKind.String)
                return contents.GetString();
            if (contents.TryGetProperty("value", out var val))
                return val.GetString();
        }

        return result.ToString();
    }

    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(string filePath, int line, int character, CancellationToken ct)
    {
        await NotifyDidOpenAsync(filePath, ct);

        var result = await SendRequestAsync("textDocument/definition", new
        {
            textDocument = new { uri = $"file://{filePath}" },
            position = new { line, character },
        }, ct);

        return ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(string filePath, int line, int character, CancellationToken ct)
    {
        await NotifyDidOpenAsync(filePath, ct);

        var result = await SendRequestAsync("textDocument/references", new
        {
            textDocument = new { uri = $"file://{filePath}" },
            position = new { line, character },
            context = new { includeDeclaration = true },
        }, ct);

        return ParseLocations(result);
    }

    private async Task NotifyDidOpenAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;

        var content = await File.ReadAllTextAsync(filePath, ct);
        var langId = Path.GetExtension(filePath).TrimStart('.') switch
        {
            "cs" => "csharp",
            "ts" or "tsx" => "typescript",
            "js" or "jsx" => "javascript",
            "py" => "python",
            "go" => "go",
            "rs" => "rust",
            "java" => "java",
            _ => "plaintext",
        };

        await SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = $"file://{filePath}", languageId = langId, version = 1, text = content }
        }, ct);
    }

    private static IReadOnlyList<LspLocation> ParseLocations(JsonElement result)
    {
        var locations = new List<LspLocation>();

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var loc in result.EnumerateArray())
                if (TryParseLocation(loc, out var l)) locations.Add(l);
        }
        else if (result.ValueKind == JsonValueKind.Object)
        {
            if (TryParseLocation(result, out var l)) locations.Add(l);
        }

        return locations;
    }

    private static bool TryParseLocation(JsonElement el, out LspLocation location)
    {
        location = default!;
        if (!el.TryGetProperty("uri", out var uri)) return false;

        var path = uri.GetString()?.Replace("file://", "") ?? "";
        var line = 0;
        var character = 0;

        if (el.TryGetProperty("range", out var range) && range.TryGetProperty("start", out var start))
        {
            line = start.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
            character = start.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
        }

        location = new LspLocation { FilePath = path, Line = line, Character = character };
        return true;
    }

    private async Task<JsonElement> SendRequestAsync(string method, object @params, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
            var header = $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n";

            var bytes = Encoding.UTF8.GetBytes(header + body);
            await _stdout.WriteAsync(Array.Empty<byte>(), ct);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);

            return await ReadResponseAsync(id, ct);
        }
        finally { _lock.Release(); }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params });
            var header = $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n";
            var bytes = Encoding.UTF8.GetBytes(header + body);
            await _stdin.BaseStream.WriteAsync(bytes, ct);
            await _stdin.BaseStream.FlushAsync(ct);
        }
        finally { _lock.Release(); }
    }

    private async Task<JsonElement> ReadResponseAsync(int expectedId, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var headerBuilder = new StringBuilder();

        while (true)
        {
            var b = _stdout.ReadByte();
            if (b < 0) throw new InvalidOperationException("LSP server closed");
            headerBuilder.Append((char)b);

            if (headerBuilder.Length >= 4 && headerBuilder.ToString().EndsWith("\r\n\r\n"))
                break;
        }

        var headerStr = headerBuilder.ToString();
        var clMatch = System.Text.RegularExpressions.Regex.Match(headerStr, @"Content-Length:\s*(\d+)");
        if (!clMatch.Success) throw new InvalidOperationException("Invalid LSP header");

        var contentLength = int.Parse(clMatch.Groups[1].Value);
        var bodyBytes = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
            read += await _stdout.ReadAsync(bodyBytes.AsMemory(read, contentLength - read), ct);

        var json = Encoding.UTF8.GetString(bodyBytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("result", out var result))
            return result.Clone();
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"LSP error: {error}");

        return default;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(3000); } }
        catch { }
        _process.Dispose();
        _lock.Dispose();
    }
}

public sealed record LspLocation
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int Character { get; init; }
    public override string ToString() => $"{FilePath}:{Line + 1}:{Character + 1}";
}
