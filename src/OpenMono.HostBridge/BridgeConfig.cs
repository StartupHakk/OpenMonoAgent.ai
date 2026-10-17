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
    /// Shared secret required by sandbox sessions to pair with the daemon
    /// (<c>host-bridge daemon</c>). Generated on first daemon start when empty
    /// and stored here (0600, same rules as every other bridge config). It is
    /// a guardrail against unrelated containers/processes pairing with the
    /// daemon — not a sandbox boundary (see docs/DEPLOYMENT-MODES.md).
    /// </summary>
    [JsonPropertyName("pair_secret")]
    public string PairSecret { get; set; } = "";

    /// <summary>
    /// How the daemon answers sandbox pairing that carries no secret/invite.
    /// "this_session" (default): a session id that was previously paired with
    /// this daemon run re-pairs automatically (logged); everything else needs
    /// an explicit approval on the host (<c>openmono host approve</c>).
    /// "strict": every approval-less pair request waits for the operator.
    /// </summary>
    [JsonPropertyName("pair_auto")]
    public string PairAuto { get; set; } = "this_session";

    /// <summary>
    /// Seconds a launcher-issued pairing invite stays valid (default 900 =
    /// 15 min). An expired invite is dead: the agent falls back to a pair
    /// request the operator approves on the host.
    /// </summary>
    [JsonPropertyName("invite_ttl_seconds")]
    public int InviteTtlSeconds { get; set; } = 900;

    /// <summary>
    /// Hard lifetime of a session token since pairing, regardless of activity
    /// (hours). 0 (default) = no cap, idle-timeout only. A positive cap forces
    /// the agent to re-request pairing — and the operator's approval lane
    /// (<see cref="PairAuto"/>) decides whether that is silent or explicit.
    /// </summary>
    [JsonPropertyName("token_max_hours")]
    public double TokenMaxHours { get; set; } = 0;

    /// <summary>
    /// Seconds an approval-less pair request waits for the operator before
    /// timing out (default 120). Test hook: a tiny value exercises the
    /// timeout path without sleeping for minutes.
    /// </summary>
    [JsonPropertyName("pair_request_wait_seconds")]
    public int PairRequestWaitSeconds { get; set; } = 120;

    /// <summary>
    /// Account host commands run as. Empty = whoever runs the bridge. A
    /// username is not a secret and may live in this file; the password for
    /// it NEVER does — it stays in memory only (prompted or via
    /// OPENMONO_HOST_PASSWORD_FILE) and is never logged.
    /// </summary>
    [JsonPropertyName("run_as")]
    public string RunAs { get; set; } = "";

    /// <summary>
    /// Privilege-escalation policy for host commands. One of
    /// "ask" (default: confirm each elevation with y/N — a yes is in-memory
    /// only for that command), "allow" (elevate without prompting), or
    /// "deny" (never escalate). Legacy JSON values are mapped on load:
    /// missing/null → ask, true → allow, false → deny.
    /// </summary>
    [JsonPropertyName("allow_sudo")]
    public string? AllowSudo { get; set; } = "ask";

    /// <summary>
    /// <see cref="AllowSudo"/> as an enum, with legacy bool/null back-compat.
    /// </summary>
    [JsonIgnore]
    public SudoPolicy SudoPolicy
    {
        get => AllowSudo?.Trim() switch
        {
            null or "" => SudoPolicy.Ask,
            var s when s.Equals("allow", StringComparison.OrdinalIgnoreCase) => SudoPolicy.Allow,
            var s when s.Equals("deny", StringComparison.OrdinalIgnoreCase) => SudoPolicy.Deny,
            _ => SudoPolicy.Ask,
        };
    }

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
            // Legacy configs hold allow_sudo as a JSON boolean (or omit it).
            // Pull the raw token out BEFORE deserializing: a bool token in a
            // string property throws, and we must survive it to normalize.
            string? rawAllowSudo = null;
            var boolToken = false;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("allow_sudo", out var v))
                {
                    rawAllowSudo = v.ValueKind switch
                    {
                        JsonValueKind.True => "allow",
                        JsonValueKind.False => "deny",
                        JsonValueKind.String => v.GetString(),
                        _ => null,
                    };
                    boolToken = v.ValueKind is JsonValueKind.True or JsonValueKind.False;
                }
            }
            // Neutralize a legacy bool token so the typed deserializer accepts it
            // (whitespace-tolerant: the file may be pretty-printed).
            if (boolToken)
                json = System.Text.RegularExpressions.Regex.Replace(
                    json, "\"allow_sudo\"\\s*:\\s*(true|false)",
                    "\"allow_sudo\":\"ask\"",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
            var cfg = JsonSerializer.Deserialize<BridgeConfig>(json, WebOptions())
                ?? new BridgeConfig();
            if (!string.IsNullOrWhiteSpace(rawAllowSudo))
                cfg.AllowSudo = rawAllowSudo;
            return cfg;
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
                // allow_sudo = "ask": every elevation confirms with y/N; a
                // yes is in-memory only for that command (config stays "ask"
                // until the operator runs openmono config host.sudo_policy).
                AllowSudo = "ask",
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
                    // silently. macOS samples ship read-only launchd/log
                    // patterns instead of the Linux systemctl/journalctl ones.
                    Allow = [.. (OperatingSystem.IsMacOS()
                        ? HostExecPolicy.SampleAllowMacOS
                        : HostExecPolicy.SampleAllow)],
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
        ["git *", "systemctl status *", "journalctl *"];

    /// <summary>
    /// macOS sample-config twin: read-only launchd/log patterns instead of
    /// the Linux systemctl/journalctl ones. Ask-by-default still prompts for
    /// each; this list only matters under an explicit allow default.
    /// </summary>
    public static readonly IReadOnlyList<string> SampleAllowMacOS =
        ["git *", "launchctl list *", "log show *"];

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
    /// True when this policy is byte-for-byte a shipped sample (default
    /// ask): the operator never customized it, so upgrades may move it to the
    /// new allow default. Anything customized is left strictly alone. Accepts
    /// the macOS sample twin as well as the Linux one.
    /// </summary>
    public bool IsUncustomizedAskSample() =>
        Default.Trim().Equals("ask", StringComparison.OrdinalIgnoreCase) &&
        (Allow.SequenceEqual(SampleAllow) || Allow.SequenceEqual(SampleAllowMacOS)) &&
        Deny.SequenceEqual(SampleDeny);
}
