using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMono.Commands;

/// <summary>
/// Top-level <c>openmono config ...</c> subcommand — non-interactive, exits after running.
///
/// <para>Two surfaces:</para>
/// <list type="bullet">
/// <item><c>openmono config get [key]</c> / <c>openmono config set &lt;key&gt; &lt;value&gt;</c>
/// read/write <c>~/.openmono/settings.json</c> by dotted path (e.g. <c>llm.endpoint</c>,
/// <c>llm.api_key</c>). The file is edited in place with <see cref="JsonNode"/>, so keys
/// that are not modeled in <see cref="OpenMono.Config.AppConfig"/> are preserved untouched.</item>
/// <item><c>openmono config backends list|add|remove|set-key</c> manage
/// <c>~/.openmono/backends.json</c> — the persisted inference backends the ACP server and
/// TUI load at startup. Keys may be persisted here (file is 0600) so a named backend
/// survives restarts without re-entering the key.</item>
/// </list>
/// </summary>
public static class ConfigCommand
{
    private static readonly string[] SecretKeyFragments = ["api_key", "apikey", "key", "token", "secret"];
    private const string DefaultBackendName = "default";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static (string Output, int Code) Run(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Length > 1 ? args[1..] : [];

        return cmd switch
        {
            "get" => Get(rest),
            "set" => Set(rest),
            "backends" => Backends(rest),
            "" or "help" or "--help" or "-h" => (Usage(), 0),
            _ => (Usage(), 2),
        };
    }

    // ── settings.json get / set ─────────────────────────────────────────────────

    private static (string Output, int Code) Get(string[] rest)
    {
        var (path, root, err) = LoadSettings();
        if (err is not null) return (err, 1);

        if (rest.Length == 0)
            return (root.ToJsonString(Indented), 0);

        if (rest.Length > 1) return ("usage: openmono config get [key]", 2);

        var key = rest[0];
        if (TryResolve(root, key, out var node))
        {
            var isSecret = IsSecretPath(key);
            return (isSecret ? $"{key} = (masked)" : NodeToText(node), 0);
        }
        return ($"{key} = (not set)", 1);
    }

    private static (string Output, int Code) Set(string[] rest)
    {
        if (rest.Length < 2)
            return ("usage: openmono config set <key> <value>\nExample: openmono config set llm.endpoint http://localhost:7474", 2);

        var (path, root, err) = LoadSettings();
        if (err is not null) return (err, 1);

        var key = rest[0];
        var value = rest.Length == 2 ? rest[1] : string.Join(' ', rest[1..]);

        if (!TrySet(root, key, ParseValue(value), out var target))
            return ($"error: could not set '{key}' in {path}", 1);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, root.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            return ($"error: could not write {path}: {ex.Message}", 1);
        }

        var isSecret = IsSecretPath(key);
        return ($"set {key} = {(isSecret ? "(masked)" : NodeToText(target))}  [{path}]", 0);
    }

    // ── backends ─────────────────────────────────────────────────────────────────

    private static (string Output, int Code) Backends(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Length > 1 ? args[1..] : [];

        var (_, root, _) = LoadSettings();
        var dataDir = AppConfigDataDirectory();
        var backendsFile = Path.Combine(dataDir, "backends.json");

        switch (sub)
        {
            case "":
            case "help":
            case "--help":
                return (BackendsUsage(), 2);

            case "list":
            {
                var entries = LoadBackendEntries(backendsFile);
                // The "default" backend always exists at runtime (from llm.endpoint);
                // show it even if it is not in the file.
                if (!entries.ContainsKey(DefaultBackendName))
                {
                    var endpoint = Resolve(root, "llm.endpoint")?.GetValue<string>() ?? "";
                    var model = Resolve(root, "llm.model")?.GetValue<string>() ?? "";
                    var hasKey = HasValue(root, "llm.api_key");
                    entries[DefaultBackendName] = (endpoint, model, "openai-compat",
                        string.IsNullOrEmpty(endpoint) ? false : true, hasKey, null);
                }

                var lines = new List<string> { "Backend (inference endpoint):" };
                foreach (var (name, e) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var label = string.IsNullOrEmpty(e.Nickname) ? name : $"{name} \"{e.Nickname}\"";
                    lines.Add($"  {label,-28} endpoint={e.Endpoint,-40} model={OrDash(e.Model)}" +
                               (e.HasKey ? "  key=set" : (e.IsLocal ? "  (local)" : "  key=NONE")));
                }
                lines.Add($"\nfile: {backendsFile}");
                return (string.Join("\n", lines), 0);
            }

            case "add":
            {
                var name = rest.Length > 0 ? rest[0] : "";
                string? endpoint = null;
                string? model = null;
                string? key = null;
                string? nickname = null;
                var providerType = "openai-compat";
                var isLocal = false;

                for (var i = 1; i < rest.Length; i++)
                {
                    var a = rest[i];
                    if (a.StartsWith("--", StringComparison.Ordinal) && i + 1 < rest.Length)
                    {
                        var next = rest[++i];
                        switch (a.ToLowerInvariant())
                        {
                            case "--endpoint": endpoint = next; break;
                            case "--model": model = next; break;
                            case "--key": key = next; break;
                            case "--provider-type": providerType = next; break;
                            case "--nickname": nickname = next; break;
                            case "--local": isLocal = next.ToLowerInvariant() is "1" or "true" or "yes"; break;
                            default: return ($"unknown flag '{a}' — see: openmono config backends", 2);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
                    return ("usage: openmono config backends add <name> --endpoint <url> [--model <m>] [--nickname <n>] [--key <k>] [--provider-type <t>] [--local true]", 2);

                SaveBackendEntry(backendsFile, name, endpoint, model, nickname, providerType, isLocal, key);
                return ($"added backend '{name}' -> {endpoint}{(key is null ? "" : " (key persisted)")}  [{backendsFile}]", 0);
            }

            case "remove":
            {
                var name = rest.Length > 0 ? rest[0] : "";
                if (name.Length == 0) return ("usage: openmono config backends remove <name>", 2);
                if (name.Equals(DefaultBackendName, StringComparison.OrdinalIgnoreCase))
                    return ("the 'default' backend (from llm.endpoint) cannot be removed; edit llm.endpoint instead.", 1);

                var fileDoc = File.Exists(backendsFile) ? JsonNode.Parse(File.ReadAllText(backendsFile)) : null;
                if (fileDoc is JsonObject o && o.TryGetPropertyValue(name, out _))
                {
                    o.Remove(name);
                    File.WriteAllText(backendsFile, o.ToJsonString(Indented));
                    return ($"removed backend '{name}'  [{backendsFile}]", 0);
                }
                return ($"backend '{name}' not found.", 1);
            }

            case "set-key":
            {
                if (rest.Length < 2) return ("usage: openmono config backends set-key <name> <key>", 2);
                var name = rest[0];
                var key = string.Join(' ', rest[1..]);

                var fileDoc = File.Exists(backendsFile) ? JsonNode.Parse(File.ReadAllText(backendsFile)) : null;
                if (fileDoc is not JsonObject o)
                    o = new JsonObject();
                if (!o.TryGetPropertyValue(name, out var entry) || entry is not JsonObject e)
                    return ($"backend '{name}' not found — add it first: openmono config backends add {name} --endpoint <url>", 1);

                if (key.Length == 0) e.Remove("api_key");
                else e["api_key"] = key;
                File.WriteAllText(backendsFile, o.ToJsonString(Indented));
                Chmod600(backendsFile);
                return (key.Length == 0
                    ? $"cleared key for '{name}'  [{backendsFile}]"
                    : $"set key for '{name}' (persisted, 0600)  [{backendsFile}]", 0);
            }

            default:
                return ($"unknown backend subcommand '{sub}' — see: openmono config backends", 2);
        }
    }

    // ── settings.json helpers ─────────────────────────────────────────────────────

    private static string AppConfigDataDirectory()
    {
        var override_ = Environment.GetEnvironmentVariable("OPENMONO_DATA_DIR");
        if (!string.IsNullOrEmpty(override_)) return override_;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono");
    }

    private static (string Path, JsonNode Root, string? Error) LoadSettings()
    {
        var path = Path.Combine(AppConfigDataDirectory(), "settings.json");
        if (!File.Exists(path))
            return (path, new JsonObject(), null);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root is not JsonObject obj)
                return (path, new JsonObject(), $"error: {path} is not a JSON object");
            return (path, obj, null);
        }
        catch (Exception ex)
        {
            return (path, new JsonObject(), $"error: could not parse {path}: {ex.Message}");
        }
    }

    private static bool TryResolve(JsonNode root, string keyPath, out JsonNode? node)
    {
        node = null;
        var current = (JsonNode?)root;
        foreach (var part in keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject obj) return false;
            if (!obj.TryGetPropertyValue(part, out current) || current is null) return false;
        }
        node = current;
        return true;
    }

    private static JsonNode? Resolve(JsonNode root, string keyPath)
        => TryResolve(root, keyPath, out var n) ? n : null;

    private static bool HasValue(JsonNode root, string keyPath)
        => TryResolve(root, keyPath, out var n)
           && n is not null
           && !(n is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0);

    private static bool TrySet(JsonNode root, string keyPath, JsonNode? value, out JsonNode? target)
    {
        target = null;
        var parts = keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        JsonNode current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current is not JsonObject obj) return false;
            if (!obj.TryGetPropertyValue(parts[i], out var child) || child is null)
            {
                child = new JsonObject();
                obj[parts[i]] = child;
            }
            if (child is not JsonObject) return false;
            current = child;
        }

        if (current is JsonObject dest)
        {
            dest[parts[^1]] = value;
            target = value;
            return true;
        }
        return false;
    }

    private static JsonNode? ParseValue(string value)
    {
        if (value.Length > 0 && (value[0] == '{' || value[0] == '['))
        {
            try { return JsonNode.Parse(value); }
            catch { /* not a JSON literal — treat as string */ }
        }
        var v = value.ToLowerInvariant();
        if (v is "true" or "false") return v == "true" ? true : false;
        if (v is "null") return null;
        if (long.TryParse(value, out var l)) return l;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return value;
    }

    private static string NodeToText(JsonNode? node)
    {
        if (node is null) return "(unset)";
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return node.ToJsonString();
    }

    private static bool IsSecretPath(string keyPath)
    {
        var last = keyPath.Split('.').Last().ToLowerInvariant();
        return SecretKeyFragments.Contains(last);
    }

    private static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    // ── backends.json helpers ─────────────────────────────────────────────────────

    private static Dictionary<string, (string Endpoint, string? Model, string ProviderType, bool IsLocal, bool HasKey, string? Nickname)>
        LoadBackendEntries(string path)
    {
        var result = new Dictionary<string, (string, string?, string, bool, bool, string?)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var endpoint = prop.Value.TryGetProperty("endpoint", out var e) ? e.GetString() ?? "" : "";
                var model = prop.Value.TryGetProperty("default_model", out var m) ? m.GetString() : null;
                var ptype = prop.Value.TryGetProperty("provider_type", out var pt) ? pt.GetString() ?? "openai-compat" : "openai-compat";
                var isLocal = prop.Value.TryGetProperty("is_local", out var il) && il.ValueKind == JsonValueKind.True;
                var hasKey = prop.Value.TryGetProperty("api_key", out var k)
                             && k.ValueKind == JsonValueKind.String
                             && !string.IsNullOrEmpty(k.GetString());
                var nickname = prop.Value.TryGetProperty("nickname", out var nk) ? nk.GetString() : null;
                result[prop.Name] = (endpoint, model, ptype, isLocal, hasKey, nickname);
            }
        }
        catch (Exception) { /* malformed file — return what we have */ }
        return result;
    }

    private static void SaveBackendEntry(string path, string name, string endpoint, string? model,
        string? nickname, string providerType, bool isLocal, string? key)
    {
        JsonNode doc = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))!
            : new JsonObject();
        if (doc is not JsonObject root)
            root = new JsonObject();

        var entry = root[name] as JsonObject ?? new JsonObject();
        entry["name"] = name;
        entry["endpoint"] = endpoint;
        if (model is not null && model.Length > 0) entry["default_model"] = model;
        if (nickname is not null && nickname.Length > 0) entry["nickname"] = nickname;
        entry["provider_type"] = providerType;
        entry["is_local"] = isLocal;
        if (key is not null && key.Length > 0) entry["api_key"] = key;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, root.ToJsonString(Indented));
        Chmod600(path);
    }

    private static void Chmod600(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"600 \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
        }
        catch { /* chmod not available — ignore */ }
    }

    // ── usage ─────────────────────────────────────────────────────────────────────

    private static string Usage() =>
"""
openmono config — read and write ~/.openmono settings without starting the agent.

Usage:
  openmono config get [key]                    print settings.json (or a dotted key, e.g. llm.endpoint)
  openmono config set <key> <value>            set a dotted key in settings.json (e.g. llm.api_key)
  openmono config backends                     show backends help
  openmono config backends list                list configured inference backends
  openmono config backends add <name> --endpoint <url> [--model <m>] [--key <k>] [--provider-type <t>] [--local true]
  openmono config backends remove <name>       remove a backend
  openmono config backends set-key <name> <key>   persist (or clear, with empty key) a backend's API key

Notes:
  - settings.json is edited in place; unknown keys are preserved.
  - 'default' backend = llm.endpoint / llm.api_key from settings.json (auto-seeded at startup).
  - backends.json is the persisted backend list the ACP server loads; keys are 0600.
  - the agent picks up backends.json on next start (restart the agent to apply).
""";

    private static string BackendsUsage() =>
"""
openmono config backends <list|add|remove|set-key>

  list                                  list configured inference backends
  add <name> --endpoint <url>           add a backend
      [--model <m>]                     default model for the backend
      [--key <k>]                       persist the API key (0600)
      [--provider-type <t>]             openai-compat (default) | anthropic
      [--local true]                    mark as a local server (no key required)
  remove <name>                         remove a backend (not 'default')
  set-key <name> <key>                  persist (or clear) a backend's API key

Examples:
  openmono config backends add local --endpoint http://localhost:7474 --model Ternary-Bonsai-2-27B-PQ2_0 --local true
  openmono config backends add relay --endpoint http://relay.openmonoagent.ai:10422 --key <key>
""";
}
