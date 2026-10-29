using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMono.Utils;

namespace OpenMono.Llm;

/// <summary>
/// In-memory registry of configured inference backends and their API keys.
///
/// <para>
/// <c>Backends</c> are persisted to <c>~/.openmono/backends.json</c>. <c>Keys</c> live in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> and are re-supplied at runtime via
/// <c>/models</c>, the ACP <c>POST /api/v1/backends</c> / <c>POST /api/v1/sessions/{id}/backend</c>
/// endpoints, or the <c>OPENMONO_API_KEY</c> env var. A key may also be persisted to
/// <c>backends.json</c> (0600) via <c>openmono config backends add --key</c> /
/// <c>set-key</c> or when the ACP endpoints save the registry — that makes a non-local
/// backend usable across restarts without re-entering the key.
/// </para>
/// </summary>
public sealed class BackendRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, BackendDefinition> _backends =
        new(StringComparer.OrdinalIgnoreCase);

    // Keys are in-memory only. Never serialized, never logged.
    private readonly ConcurrentDictionary<string, string> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default backend name — the one a session uses when no backend is set.</summary>
    public const string DefaultBackendName = "default";

    public BackendRegistry() { }

    /// <summary>
    /// Constructor that seeds the registry from a <c>backends.json</c> file (if present)
    /// plus an explicit <c>default</c> definition (endpoint/model from <c>AppConfig.Llm</c>).
    /// Persisted <c>api_key</c> fields (when present) are loaded into the in-memory key store.
    /// </summary>
    public BackendRegistry(BackendDefinition? defaultBackend)
    {
        if (defaultBackend is not null)
            Add(defaultBackend);
    }

    /// <summary>
    /// When running inside a container (signalled by the <c>OPENMONO_HOST_WORKSPACE</c> env var
    /// the launcher sets), loopback endpoints on local backends are unreachable from inside
    /// (the container's own <c>localhost</c> is not the host). Rewrite their host to
    /// <paramref name="hostAlias"/> (the Docker host-gateway address, e.g.
    /// <c>host.docker.internal</c>), keeping port and path. The persisted
    /// <c>backends.json</c> is left untouched.
    /// </summary>
    public void RemapLoopbackEndpoints(string hostAlias)
    {
        foreach (var (name, def) in _backends)
        {
            if (!def.IsLocal) continue;
            if (!Uri.TryCreate(def.Endpoint, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && uri.Host != "127.0.0.1")
                continue;
            _backends[name] = def with { Endpoint = def.Endpoint.Replace(uri.Host, hostAlias, StringComparison.OrdinalIgnoreCase) };
        }
    }

    public void LoadFromFile(string path)
    {
        if (!File.Exists(path)) return;

        string? json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"BackendRegistry: could not read {path}: {ex.Message}");
            return;
        }

        try
        {
            var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var name = prop.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    Log.Warn($"BackendRegistry: '{name}' is not an object — skipped.");
                    continue;
                }

                var def = new BackendDefinition
                {
                    Name = name,
                    Endpoint = prop.Value.TryGetProperty("endpoint", out var epEl)
                        ? epEl.GetString() ?? ""
                        : throw new JsonException($"'{name}' requires 'endpoint'."),
                    DefaultModel = prop.Value.TryGetProperty("default_model", out var dmEl)
                        ? dmEl.GetString()
                        : null,
                    ProviderType = prop.Value.TryGetProperty("provider_type", out var ptEl)
                        ? ptEl.GetString() ?? "openai-compat"
                        : "openai-compat",
                    IsLocal = prop.Value.TryGetProperty("is_local", out var ilEl)
                        ? ilEl.GetBoolean()
                        : false,
                    Nickname = prop.Value.TryGetProperty("nickname", out var nEl)
                        ? nEl.GetString()
                        : null,
                };

                Add(def);

                if (prop.Value.TryGetProperty("api_key", out var keyEl)
                    && keyEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(keyEl.GetString()))
                {
                    SetKey(name, keyEl.GetString()!);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"BackendRegistry: could not parse {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the backend definitions to <c>path</c> (0600). Keys currently held in the
    /// in-memory key store are written alongside their backend so a restart does not lose
    /// them (the file is 0600).
    /// </summary>
    public void SaveToFile(string path)
    {
        var dict = new Dictionary<string, BackendEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _backends)
            dict[kv.Key] = new BackendEntry(kv.Value, GetKey(kv.Key));

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        string json = JsonSerializer.Serialize(dict, options);

        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);

        File.WriteAllText(path, json);

        // Set 0600 (owner read/write) via chmod.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"600 \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();
        }
        catch
        {
            // chmod not available (e.g. Windows) — ignore.
        }
    }

    public void Add(BackendDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Endpoint))
            throw new ArgumentException("Backend name and endpoint are required.", nameof(definition));

        _backends[definition.Name] = definition;
    }

    public void Remove(string name)
    {
        if (_backends.TryRemove(name, out _))
            _keys.TryRemove(name, out _);
    }

    public BackendDefinition? Get(string name) => _backends.GetValueOrDefault(name);

    public bool Has(string name) => _backends.ContainsKey(name);

    public IReadOnlyCollection<BackendDefinition> All => _backends.Values.ToList();

    // ── KEY API (persisted to backends.json via SaveToFile) ──────────────────

    public void SetKey(string name, string key) => _keys[name] = key;

    public string? GetKey(string name) => _keys.GetValueOrDefault(name);

    public bool HasKey(string name) => _keys.ContainsKey(name);

    public void RemoveKey(string name) => _keys.TryRemove(name, out _);

    /// <summary>Resolves a backend by name or nickname (case-insensitive).</summary>
    public BackendDefinition? Resolve(string nameOrNickname)
    {
        if (string.IsNullOrWhiteSpace(nameOrNickname)) return null;
        if (_backends.TryGetValue(nameOrNickname, out var direct)) return direct;
        return _backends.Values.FirstOrDefault(b =>
            string.Equals(b.Nickname, nameOrNickname, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _backends.Clear();
        _keys.Clear();
    }

    /// <summary>
    /// Serialization shape for <c>backends.json</c>: the public <see cref="BackendDefinition"/>
    /// fields plus an optional <c>api_key</c> (written only when a key is held in memory).
    /// </summary>
    private sealed class BackendEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("endpoint")]
        public string? Endpoint { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("default_model")]
        public string? DefaultModel { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("provider_type")]
        public string? ProviderType { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("is_local")]
        public bool? IsLocal { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("api_key")]
        public string? ApiKey { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("nickname")]
        public string? Nickname { get; init; }

        public BackendEntry(BackendDefinition def, string? apiKey)
        {
            Name = def.Name;
            Endpoint = def.Endpoint;
            DefaultModel = def.DefaultModel;
            ProviderType = def.ProviderType;
            IsLocal = def.IsLocal;
            Nickname = def.Nickname;
            ApiKey = apiKey;
        }
    }
}
