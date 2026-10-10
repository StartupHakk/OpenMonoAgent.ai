using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMono.HostBridge;

/// <summary>
/// Operator configuration for the host bridge (~/.openmono/host-bridge.json).
/// Placeholders only — this file never holds real secrets; the inference API
/// key stays in ~/.openmono/settings.json like the rest of the system.
/// </summary>
public sealed class BridgeConfig
{
    [JsonPropertyName("acp")]
    public string Acp { get; set; } = "";

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 300_000;

    [JsonPropertyName("log_dir")]
    public string LogDir { get; set; } = "";

    [JsonPropertyName("permission_scope")]
    public string PermissionScope { get; set; } = "session";

    [JsonPropertyName("tool_default")]
    public string ToolDefault { get; set; } = "ask";

    [JsonPropertyName("tools")]
    public Dictionary<string, string> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("host_exec")]
    public HostExecPolicy HostExec { get; set; } = new();

    /// <summary>
    /// Account host commands run as. Empty = whoever runs the bridge. A
    /// username is not a secret and may live in this file; the password for
    /// it NEVER does — it stays in memory only (prompted or via
    /// OPENMONO_HOST_PASSWORD_FILE) and is never logged.
    /// </summary>
    [JsonPropertyName("run_as")]
    public string RunAs { get; set; } = "";

    /// <summary>
    /// Whether host commands may escalate with sudo. Null = never asked:
    /// the bridge prompts once on first run (or <c>--init</c>) and persists
    /// the answer. False = sudo is never used, period.
    /// </summary>
    [JsonPropertyName("allow_sudo")]
    public bool? AllowSudo { get; set; }

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openmono", "host-bridge.json");

    public static BridgeConfig Load(string? path, TextWriter log)
    {
        var resolved = path ?? DefaultPath;
        if (!File.Exists(resolved))
            return new BridgeConfig();

        try
        {
            var json = File.ReadAllText(resolved);
            return JsonSerializer.Deserialize<BridgeConfig>(json, WebOptions())
                ?? new BridgeConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"[bridge] WARNING: ignoring unreadable config {resolved}: {ex.Message}");
            return new BridgeConfig();
        }
    }

    public static bool WriteDefaultIfMissing(string? path, TextWriter log)
    {
        var resolved = path ?? DefaultPath;
        if (File.Exists(resolved))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            var sample = new BridgeConfig
            {
                // allow_sudo intentionally left unset (null): the bridge asks
                // once on first run / --init and persists the answer.
                RunAs = "",
                Tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bash"] = "allow",
                    ["FileRead"] = "allow",
                    ["Glob"] = "allow",
                    ["Grep"] = "allow",
                },
                HostExec = new HostExecPolicy
                {
                    // ask-by-default: every host command confirms with y/N
                    // unless the operator explicitly chose "allow routine"
                    // at install (OPENMONO_HOST_EXEC_DEFAULT=allow). The deny
                    // list still blocks destructive patterns, sudo still needs
                    // its own opt-in, and every command is audited. Install
                    // flips this to "allow" only on explicit opt-in — never
                    // silently.
                    Allow = [.. HostExecPolicy.SampleAllow],
                    Deny = [.. HostExecPolicy.SampleDeny],
                    Default = "ask",
                },
            };
            File.WriteAllText(resolved,
                JsonSerializer.Serialize(sample, IndentedWebOptions()));
            TightenPermissions(resolved);
            log.WriteLine($"[bridge] wrote sample config to {resolved}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"[bridge] WARNING: could not write sample config: {ex.Message}");
            return false;
        }
    }

    public static void Save(string? path, BridgeConfig config)
    {
        var resolved = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        File.WriteAllText(resolved,
            JsonSerializer.Serialize(config, IndentedWebOptions()));
        TightenPermissions(resolved);
    }

    /// <summary>
    /// Legacy upgrade hook — now a no-op. We used to move untouched stock
    /// "ask" samples to "allow"; that silent escalation is retired. "Always"
    /// now requires explicit opt-in at install
    /// (OPENMONO_HOST_EXEC_DEFAULT=allow). Existing configs are never
    /// changed here. Returns false always (kept for call-site compat).
    /// </summary>
    public static bool MigrateStockAskToAllow(string? path, BridgeConfig config, TextWriter log)
    {
        return false;
    }

    /// <summary>
    /// Owner read/write only — same convention as settings.json and
    /// docker/.env elsewhere in this project.
    /// </summary>
    internal static void TightenPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
        }
    }

    [JsonIgnore]
    public string ResolvedLogDir =>
        string.IsNullOrWhiteSpace(LogDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openmono", "host-bridge", "logs")
            : Environment.ExpandEnvironmentVariables(LogDir);

    internal static JsonSerializerOptions WebOptions() =>
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    internal static JsonSerializerOptions IndentedWebOptions() =>
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>
/// Allow/deny policy for bare-metal HOST_EXEC commands.
/// Deny is evaluated first; then allow; then <see cref="Default"/>.
/// </summary>
public sealed class HostExecPolicy
{
    /// <summary>Allow patterns shipped in the sample config.</summary>
    public static readonly IReadOnlyList<string> SampleAllow =
        ["git *", "docker *", "systemctl status *", "journalctl *", "curl *"];

    /// <summary>Deny patterns shipped in the sample config.</summary>
    public static readonly IReadOnlyList<string> SampleDeny =
        ["rm -rf *", "shutdown *", "reboot *", "mkfs *"];

    [JsonPropertyName("allow")]
    public List<string> Allow { get; set; } = [];

    [JsonPropertyName("deny")]
    public List<string> Deny { get; set; } = [];

    [JsonPropertyName("default")]
    public string Default { get; set; } = "ask";

    /// <summary>
    /// True when this policy is byte-for-byte the OLD shipped sample (default
    /// ask): the operator never customized it, so upgrades may move it to the
    /// new allow default. Anything customized is left strictly alone.
    /// </summary>
    public bool IsUncustomizedAskSample() =>
        Default.Trim().Equals("ask", StringComparison.OrdinalIgnoreCase) &&
        Allow.SequenceEqual(SampleAllow) &&
        Deny.SequenceEqual(SampleDeny);
}
