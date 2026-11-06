using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenMono.Win.Inference;

namespace OpenMono.Win.Launcher;

// `openmono diag`: one zip a user can attach to an issue. GPU, VRAM and the
// driver banner, the installed llama.cpp builds, the last llama-server log
// lines, the resolved inference state, doctor output, and
// settings.json with every secret-like value removed. The user's home path
// is replaced with "~" everywhere, so the bundle names no account.
public static partial class DiagnosticsRedactor
{
    private static readonly string[] SecretKeyFragments =
        ["key", "secret", "token", "password", "invite", "credential", "auth"];

    public const string Redacted = "[redacted]";

    public static string RedactText(string text, string homeDir)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            foreach (var form in new[] { homeDir, homeDir.Replace('\\', '/'), homeDir.Replace('/', '\\') }.Distinct())
            {
                result = result.Replace(form, "~", StringComparison.OrdinalIgnoreCase);
            }
        }

        result = SecretAssignment().Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + Redacted);
        result = BearerToken().Replace(result, "Bearer " + Redacted);
        result = OpenAiStyleKey().Replace(result, Redacted);
        return result;
    }

    public static string RedactSettingsJson(string json, string homeDir)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return "(settings.json is not valid JSON; omitted)";
        }

        if (node is null)
        {
            return "{}";
        }

        Scrub(node);
        return RedactText(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), homeDir);
    }

    private static void Scrub(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecretName(name) && obj[name] is JsonValue)
                    {
                        obj[name] = Redacted;
                    }
                    else if (obj[name] is { } child)
                    {
                        Scrub(child);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        Scrub(item);
                    }
                }

                break;
        }
    }

    private static bool IsSecretName(string name) =>
        SecretKeyFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<name>(?i:[A-Za-z0-9_\-]*(api[_-]?key|token|secret|password|invite|pair[_-]?secret)[A-Za-z0-9_\-]*))(?<sep>""?\s*[:=]\s*""?)(?<value>[^\s""',}]+)")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex OpenAiStyleKey();
}

public sealed class DiagnosticsBundle
{
    private readonly LauncherDeps _deps;

    public DiagnosticsBundle(LauncherDeps deps)
    {
        _deps = deps;
    }

    public async Task<IReadOnlyDictionary<string, string>> CollectAsync(
        IReadOnlyList<DoctorCheck> doctor,
        CancellationToken ct = default)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = new StringBuilder();
        env.AppendLine($"os: {Environment.OSVersion}");
        env.AppendLine($"dotnet: {Environment.Version}");
        env.AppendLine($"docker server: {await RunTextAsync("docker", "version --format {{.Server.Version}}", ct)}");
        foreach (var gpu in await _deps.Gpu.ProbeAsync(ct))
        {
            env.AppendLine($"gpu: {gpu.Name}, {gpu.DedicatedVramBytes / (1024 * 1024)} MiB, nvidia={gpu.IsNvidia}");
        }

        files["environment.txt"] = env.ToString();
        files["nvidia-smi.txt"] = await RunTextAsync("nvidia-smi", "", ct);

        var root = LlamaCppInstaller.RootFor(_deps.HomeDir, _deps.GetEnv);
        var builds = LlamaCppInstaller.ListInstalled(root);
        files["llama-cpp.txt"] = builds.Count > 0
            ? string.Join(Environment.NewLine, builds.Select(b => $"{b.Id}: {b.ServerPath}"))
            : $"(no llama.cpp build installed in {root})";

        var store = new InferenceStateStore(_deps.HomeDir);
        var logFile = store.Load()?.LogFile ?? Path.Combine(_deps.HomeDir, ".openmono", "win", "llama-server.log");
        files["llama-server-log.txt"] = LlamaServerService.Tail(logFile, 200);
        files["doctor.txt"] = string.Join(Environment.NewLine,
            doctor.Select(c => $"[{c.Level.ToString().ToLowerInvariant()}] {c.Name}: {c.Detail}"));

        files["inference.json"] = File.Exists(store.FilePath) ? File.ReadAllText(store.FilePath) : "(no inference state yet)";

        var settingsPath = HostSettings.PathFor(_deps.HomeDir);
        files["settings.redacted.json"] = File.Exists(settingsPath)
            ? DiagnosticsRedactor.RedactSettingsJson(File.ReadAllText(settingsPath), _deps.HomeDir)
            : "(no settings.json)";

        // llama-server can echo its own command line, including --api-key.
        var apiKey = _deps.GetEnv(LlamaCppConstants.ApiKeyEnvVar);
        return files.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var text = DiagnosticsRedactor.RedactText(kv.Value, _deps.HomeDir);
                return string.IsNullOrEmpty(apiKey) ? text : text.Replace(apiKey, DiagnosticsRedactor.Redacted, StringComparison.Ordinal);
            },
            StringComparer.Ordinal);
    }

    public static void WriteZip(string zipPath, IReadOnlyDictionary<string, string> files)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private async Task<string> RunTextAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            var result = await _deps.Cli.RunAsync(file, args, ct);
            var text = (result.StandardOutput + result.StandardError).Trim();
            return result.Succeeded ? text : $"(exit {result.ExitCode}) {text}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"(not available: {ex.Message})";
        }
    }
}
