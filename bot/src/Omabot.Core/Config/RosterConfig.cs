// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Omabot.Common;

namespace Omabot.Config;

/// <summary>roster.json: service settings, never secrets (plan 14.2).</summary>
public sealed class RosterConfig
{
    /// <summary>local, web-single or web-multi (plan 6.8).</summary>
    public string Mode { get; set; } = "local";
    public ApiSettings Api { get; set; } = new();
    public GatewaySettings Gateway { get; set; } = new();
    public PlacementSettings Placement { get; set; } = new();
    public ApprovalSettings Approvals { get; set; } = new();
    public HostExecSettings HostExec { get; set; } = new();
    public BotsSettings Bots { get; set; } = new();
    public DesktopSettings Desktops { get; set; } = new();
    public EgressSettings Egress { get; set; } = new();
    public List<string> WorkspaceRoots { get; set; } = ["/srv/work"];
    public RoutineSettings Routines { get; set; } = new();
    public HandoffSettings Handoffs { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public WebSettings Web { get; set; } = new();
    public FleetSettings Fleet { get; set; } = new();
    public NotifySettings Notify { get; set; } = new();
    public ImageSettings Images { get; set; } = new();
    public bool StopBotsOnExit { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RosterConfig Load(RosterPaths paths)
    {
        if (!File.Exists(paths.ConfigFile)) return new RosterConfig();
        var cfg = JsonSerializer.Deserialize<RosterConfig>(File.ReadAllText(paths.ConfigFile), Json)
            ?? new RosterConfig();
        cfg.Validate();
        return cfg;
    }

    public RosterConfig Clone() => JsonSerializer.Deserialize<RosterConfig>(JsonSerializer.Serialize(this, Json), Json)!;

    public void Save(RosterPaths paths)
    {
        Validate();
        SecureFile.WriteAllTextAtomic(paths.ConfigFile, JsonSerializer.Serialize(this, Json) + "\n");
    }

    /// <summary>Throws with a plain message when a setting is out of range.</summary>
    public void Validate()
    {
        if (Mode is not ("local" or "web-single" or "web-multi"))
            throw new InvalidDataException("roster.json mode must be local, web-single or web-multi.");
        if (Placement.Mode is not ("one_host" or "fleet"))
            throw new InvalidDataException("roster.json placement.mode must be one_host or fleet.");
        if (Gateway.PerBotInFlight is < 1 or > 8)
            throw new InvalidDataException("gateway.per_bot_in_flight must be 1 to 8.");
        if (Routines.MinIntervalMinutes < 5)
            throw new InvalidDataException("routines.min_interval_minutes cannot be below 5.");
        if (Routines.SlotShare is <= 0 or > 1)
            throw new InvalidDataException("routines.slot_share must be above 0 and at most 1.");
        if (Routines.MaxJitterSeconds is < 0 or > 300)
            throw new InvalidDataException("routines.max_jitter_seconds must be 0 to 300.");
        if (Routines.MaxPerBot is < 1 or > 200)
            throw new InvalidDataException("routines.max_per_bot must be 1 to 200.");
        if (Routines.MaxRunMinutes is < 1 or > 240)
            throw new InvalidDataException("routines.max_run_minutes must be 1 to 240.");
        if (Routines.OutputCapBytes is < 256 or > 65536)
            throw new InvalidDataException("routines.output_cap_bytes must be 256 to 65536.");
        if (Handoffs.MaxDepth is < 1 or > 8) throw new InvalidDataException("handoffs.max_depth must be 1 to 8.");
        if (Handoffs.MaxChildrenPerTurn is < 1 or > 16) throw new InvalidDataException("handoffs.max_children_per_turn must be 1 to 16.");
        if (Handoffs.MaxLivePerTree is < 1 or > 64) throw new InvalidDataException("handoffs.max_live_per_tree must be 1 to 64.");
        if (Handoffs.TreeTurnBudget is < 1 or > 500) throw new InvalidDataException("handoffs.tree_turn_budget must be 1 to 500.");
        if (Handoffs.TreeTokenBudget < 1000) throw new InvalidDataException("handoffs.tree_token_budget must be at least 1000.");
        if (Approvals.PauseTimeoutMinutes is < 1 or > 60) throw new InvalidDataException("approvals.pause_timeout_minutes must be 1 to 60.");
        if (Approvals.ApprovalTtlMinutes is < 1 or > 1440) throw new InvalidDataException("approvals.approval_ttl_minutes must be 1 to 1440.");
        if (Approvals.RemindAfterHours < 1 || Approvals.CancelAfterHours <= Approvals.RemindAfterHours || Approvals.CancelAfterHours > 168)
            throw new InvalidDataException("approvals.remind_after_hours must be at least 1 and below approvals.cancel_after_hours (at most 168).");
        foreach (var a in Approvals.ApproverBots)
        {
            if (!Names.IsSafeSlug(Names.Slug(a.Bot))) throw new InvalidDataException($"approvals.approver_bots bot \"{a.Bot}\" is not a bot name.");
            foreach (var k in a.Kinds)
                if (Array.IndexOf(ApprovalSettings.BotApprovableKinds, k) < 0)
                    throw new InvalidDataException($"approvals.approver_bots kind \"{k}\" cannot be given to a bot; allowed: {string.Join(", ", ApprovalSettings.BotApprovableKinds)}.");
        }
        if (HostExec.InviteTtlSeconds is < 300 or > 86400) throw new InvalidDataException("host_exec.invite_ttl_seconds must be 300 to 86400.");
        if (Gateway.ToolLimit is < 1 or > 128) throw new InvalidDataException("gateway.tool_limit must be 1 to 128 (many servers refuse more).");
        if (Gateway.SpillBytes is not 0 and (< 2048 or > 1048576)) throw new InvalidDataException("gateway.spill_bytes must be 0 (off) or 2048 to 1048576.");
        foreach (var (name, p) in Bots.Presets)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,31}$")) throw new InvalidDataException($"bots.presets name \"{name}\" must be lower case letters, digits and dashes.");
            if (p.Temperature is < 0 or > 2 || p.TopP is <= 0 or > 1 || p.TopK is < 0 or > 1000 || p.PresencePenalty is < -2 or > 2 || p.MinP is < 0 or > 1 || p.RepetitionPenalty is < 0.5 or > 2)
                throw new InvalidDataException($"bots.presets {name} has a value out of range.");
        }
        if (!Uri.TryCreate(HostExec.AdminUrl, UriKind.Absolute, out var admin) || admin.Scheme != "http" || !System.Net.IPAddress.TryParse(admin.Host, out var adminIp) || !System.Net.IPAddress.IsLoopback(adminIp))
            throw new InvalidDataException("host_exec.admin_url must be http on a loopback address; the pair secret never leaves the host.");
        if (Gateway.Slots is not null && Gateway.Slots != "auto" && !int.TryParse(Gateway.Slots, out _))
            throw new InvalidDataException("gateway.slots must be auto or a measured number.");
        if (Gateway.DefaultPriority is not ("interactive" or "assignment" or "routine" or "background"))
            throw new InvalidDataException("gateway.default_priority must be interactive, assignment, routine or background.");
        foreach (var b in Gateway.Backends)
        {
            if (!Names.IsSafeSlug(b.Name)) throw new InvalidDataException($"gateway.backends name \"{b.Name}\" must be lower case letters, digits and dashes.");
            if (b.Kind is not ("strata" or "llama" or "openai")) throw new InvalidDataException($"gateway.backends {b.Name} kind must be strata, llama or openai.");
        }
        if (Gateway.BindAddress is "127.0.0.1" or "localhost" or "::1")
            throw new InvalidDataException("gateway.bind_address must be the Docker bridge gateway, never loopback (lesson L3).");
        Omabot.Egress.Networks.Check(this);
        if (!(System.Net.IPAddress.TryParse(Api.BindAddress, out var apiIp) && System.Net.IPAddress.IsLoopback(apiIp)))
            throw new InvalidDataException("api.bind_address must be a loopback address; web modes reach it through the HTTPS front on the same host.");
        if (Web.SessionHours is < 1 or > 24 || Web.IdleMinutes is < 5 or > 240 || Web.IdleMinutes > Web.SessionHours * 60)
            throw new InvalidDataException("web.session_hours must be 1 to 24 and web.idle_minutes 5 to 240, no longer than the session.");
        if (Web.MaxFailedLogins is < 3 or > 20 || Web.LockoutMinutes is < 1 or > 1440 || Web.AddressFailures is < 5 or > 500 || Web.AddressWindowMinutes is < 1 or > 1440)
            throw new InvalidDataException("web.max_failed_logins (3 to 20), web.lockout_minutes (1 to 1440), web.address_failures (5 to 500) and web.address_window_minutes (1 to 1440) are out of range.");
        if (Web.LoginMethod is not ("password_totp" or "passkey"))
            throw new InvalidDataException("web.login_method must be password_totp or passkey.");
        if (Fleet.MaxNodes is < 1 or > 60 || Fleet.ControllerPort is < 1024 || Fleet.ControllerPort + Fleet.MaxNodes > 65535 || Fleet.LeaseSeconds is < 15 or > 600)
            throw new InvalidDataException("fleet.max_nodes (1 to 60), fleet.controller_port and fleet.lease_seconds (15 to 600) are out of range.");
        if (Placement.Mode == "fleet" && string.IsNullOrWhiteSpace(Fleet.EnabledBy))
            throw new InvalidDataException("placement.mode fleet needs fleet.enabled_by: the person who turned fleet mode on.");
        if (Bots.Tenant is not null && !System.Text.RegularExpressions.Regex.IsMatch(Bots.Tenant, "^u(0[1-9]|[1-5][0-9]|60)$"))
            throw new InvalidDataException("bots.tenant must be u01 to u60 (set by oma-bot users add in web-multi).");
        if (Bots.Tenant is not null && (Mode != "local" || HostExec.OpenFirewall))
            throw new InvalidDataException("A web-multi user roster runs in local mode and never opens agent-host to its bots.");
        if (!Uri.TryCreate(Web.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != "https")
            throw new InvalidDataException("web.source_url must be an https address.");
        foreach (var root in WorkspaceRoots)
        {
            if (!Path.IsPathRooted(root) || root.StartsWith('~'))
                throw new InvalidDataException("workspace_roots entries must be absolute paths.");
        }
    }
}

public sealed class ApiSettings
{
    public int Port { get; set; } = 7477;
    public string BindAddress { get; set; } = "127.0.0.1";
}

public sealed class GatewaySettings
{
    public int Port { get; set; } = 7478;
    /// <summary>The roster network's gateway address (the first address of bots.subnet). Never loopback.</summary>
    public string BindAddress { get; set; } = "172.30.240.1";
    /// <summary>Relay endpoint for the main box, http://relay-host:port (decision D5).</summary>
    public string? Upstream { get; set; }
    public string UpstreamKeyFile { get; set; } = "secrets/inference.key";
    /// <summary>auto, or a measured number written by gateway bench. Never invented.</summary>
    public string? Slots { get; set; } = "auto";
    public int PerBotInFlight { get; set; } = 2;
    /// <summary>Builder choice B1 (D20): plain http to a non-loopback upstream needs this flag.</summary>
    public bool AllowPlainHttpUpstream { get; set; }
    /// <summary>Capability labels of the implicit main backend built from upstream (builder choice B22).</summary>
    public List<string> UpstreamLabels { get; set; } = ["class=main-3090"];
    /// <summary>Node class holding the measured numbers for the implicit main backend.</summary>
    public string? UpstreamNodeClass { get; set; }
    /// <summary>strata, llama or openai. Decides how slots are discovered and whether slot affinity is sent.</summary>
    public string UpstreamKind { get; set; } = "strata";
    /// <summary>Priority used when the gateway has no turn context for a bot (builder choice B21).</summary>
    public string DefaultPriority { get; set; } = "assignment";
    public int SlotRefreshSeconds { get; set; } = 300;
    public List<NodeClass> NodeClasses { get; set; } = [];
    public List<BackendSettings> Backends { get; set; } = [];
    public int UnreachableAfterSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public int ToolLimit { get; set; } = 64;
    public int SpillBytes { get; set; } = 16384;
    /// <summary>Repairs tool calls in replies to requests that offered tools (plan 7.8).</summary>
    public bool RepairToolCalls { get; set; } = true;
    /// <summary>SearXNG on the inference server, reached through the relay (plan 7.7); empty means no search for bots.</summary>
    public string? SearchUpstream { get; set; }
    /// <summary>Scrapling on the inference server, reached through the relay; empty means no scrape for bots.</summary>
    public string? ScrapeUpstream { get; set; }
}

public sealed class NodeClass
{
    public string Name { get; set; } = "";
    public string? Gpu { get; set; }
    public string? Ram { get; set; }
    public string? Backend { get; set; }
    public string? Model { get; set; }
    /// <summary>Measured only (task C1). Null until measured.</summary>
    public int? MeasuredSlots { get; set; }
    public int? ContextPerSlot { get; set; }
    public double? TokensPerSecond { get; set; }
    public double? PromptTokensPerSecond { get; set; }
    public double? LatencySeconds { get; set; }
    /// <summary>f(S) per slot level, from gateway bench only.</summary>
    public Dictionary<string, double> SpeedFactors { get; set; } = [];
    public string? MeasuredOn { get; set; }
}

public sealed class BackendSettings
{
    public string Name { get; set; } = "main";
    public string Endpoint { get; set; } = "";
    public string KeyFile { get; set; } = "secrets/inference.key";
    public string? NodeClass { get; set; }
    public List<string> Labels { get; set; } = [];
    public Dictionary<string, string> Models { get; set; } = [];
    /// <summary>strata, llama or openai.</summary>
    public string Kind { get; set; } = "strata";
    /// <summary>Send id_slot to llama-server so a bot keeps its cached prompt (sticky slots).</summary>
    public bool SlotAffinity { get; set; }
}

public sealed class PlacementSettings
{
    public string Mode { get; set; } = "one_host";
}

public sealed class ApprovalSettings
{
    /// <summary>
    /// Card kinds an approver bot may be named for. Host commands, questions to
    /// Spencer, routine and instruction changes, desktop actions and fleet jobs
    /// stay with a person (builder choice B54).
    /// </summary>
    public static readonly string[] BotApprovableKinds = ["permission", "playbook", "toggle_mode", "user_input", "plan"];

    /// <summary>Builder choice B7: empty, Spencer approves everything. The team file's approvers list adds to it.</summary>
    public List<ApproverBot> ApproverBots { get; set; } = [];
    public int PauseTimeoutMinutes { get; set; } = 10;
    public int ApprovalTtlMinutes { get; set; } = 15;
    /// <summary>Tools no team file auto rule may answer (builder choice B55); their pauses always show a card.</summary>
    public List<string> AlwaysAsk { get; set; } = ["HostExec", "Bash", "FileWrite", "ApplyPatch"];
    public int RemindAfterHours { get; set; } = 4;
    public int CancelAfterHours { get; set; } = 24;
    /// <summary>Hosts a command may not mention (hard deny, plan 7.6), matched as whole host names.</summary>
    public List<string> HardDenyHosts { get; set; } = ["facebook.com", "fb.com", "x.com", "twitter.com", "t.co"];
}

/// <summary>agent-host (OMA's host bridge) for bots whose team entry sets host_exec (plan 7.6).</summary>
public sealed class HostExecSettings
{
    /// <summary>Where the roster mints invites. agent-host listens on loopback and on the Docker bridges.</summary>
    public string AdminUrl { get; set; } = "http://127.0.0.1:7476";
    /// <summary>The address bots use; empty means the gateway bridge address on port 7476.</summary>
    public string? BotUrl { get; set; }
    /// <summary>agent-host's config file holding pair_secret; empty means ~/.openmono/host-bridge.json.</summary>
    public string? BridgeConfig { get; set; }
    public int InviteTtlSeconds { get; set; } = 86400;
    /// <summary>Adds the firewall rule that lets the roster network reach agent-host on port 7476. Set it when a bot has host_exec.</summary>
    public bool OpenFirewall { get; set; }
}

public sealed class ApproverBot
{
    public string Bot { get; set; } = "";
    public List<string> Kinds { get; set; } = [];
}

public sealed class BotsSettings
{
    public int PortBase { get; set; } = 7500;
    public int PortCount { get; set; } = 100;
    public string Image { get; set; } = "openmono-agent:latest";
    /// <summary>The internal roster network (no default route); bots reach only the gateway, agent-host and the egress gate.</summary>
    public string Network { get; set; } = "oma-roster";
    public string Subnet { get; set; } = "172.30.240.0/24";
    /// <summary>Linux bridge name, so firewall rules can key on it; "br-" so agent-host binds its address.</summary>
    public string Bridge { get; set; } = "br-omabot0";
    public int ContainerUid { get; set; } = 1000;
    /// <summary>Set only in a web-multi user roster (u01 to u60): the supervisor prefixes container names with it and keeps to its own containers.</summary>
    public string? Tenant { get; set; }
    /// <summary>Sampling presets a bot can name with preset: in the team file (OMA's OPENMONO_MODEL_PRESET). Starting points to tune by measurement.</summary>
    public Dictionary<string, SamplingPreset> Presets { get; set; } = new(StringComparer.Ordinal)
    {
        ["chat"] = new() { Temperature = 0.7, TopP = 0.8, TopK = 20, PresencePenalty = 1.5 },
        ["tools"] = new() { Temperature = 0.4, TopP = 0.8, TopK = 20, PresencePenalty = 1.0 },
    };
}

public sealed class SamplingPreset
{
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.8;
    public int TopK { get; set; } = 20;
    public double PresencePenalty { get; set; } = 1.5;
    public double MinP { get; set; }
    public double RepetitionPenalty { get; set; } = 1.0;
}

public sealed class DesktopSettings
{
    public int PortBase { get; set; } = 7600;
    public int Max { get; set; } = 8;
    /// <summary>Built from docker/desktop/Dockerfile, whose base image is pinned by digest.</summary>
    public string Image { get; set; } = "oma-roster-desktop:latest";
    public int LeaseMinutes { get; set; } = 30;
    public int IdleMinutes { get; set; } = 20;
    public int PoolSize { get; set; } = 2;
    public string Memory { get; set; } = "2g";
    public double Cpu { get; set; } = 1.5;
    public int Pids { get; set; } = 1024;
    public string User { get; set; } = "1000:1000";
    /// <summary>noVNC and the desktop MCP server inside the desktop container.</summary>
    public int NoVncPort { get; set; } = 6901;
    public int McpPort { get; set; } = 8000;
    public string Network { get; set; } = "oma-desk";
    public string Subnet { get; set; } = "172.30.241.0/24";
    public string Bridge { get; set; } = "br-omabot1";
}

public sealed class EgressSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 3128;
    /// <summary>The gate's name on the roster and desktop networks.</summary>
    public string Host { get; set; } = "egress";
    public List<string> BlockedHosts { get; set; } = ["x.com", "twitter.com", "facebook.com", "fb.com", "instagram.com"];
    /// <summary>Built from docker/egress/Dockerfile.</summary>
    public string Image { get; set; } = "oma-roster-egress:latest";
    /// <summary>The only network with a route out; only the gate joins it.</summary>
    public string Network { get; set; } = "oma-egress";
    public string Subnet { get; set; } = "172.30.242.0/24";
    public string Bridge { get; set; } = "br-omabot2";
    public List<int> Ports { get; set; } = [80, 443];
    public int AlarmRefusals { get; set; } = 20;
    public int AlarmMinutes { get; set; } = 10;
    /// <summary>required: the supervisor refuses to start containers when the firewall rules are missing. off is Spencer's explicit override.</summary>
    public string FirewallCheck { get; set; } = "required";
}

public sealed class RoutineSettings
{
    public int MinIntervalMinutes { get; set; } = 5;
    public double SlotShare { get; set; } = 0.5;
    public int CatchUpHours { get; set; } = 12;
    public int MaxJitterSeconds { get; set; } = 30;
    public int MaxPerBot { get; set; } = 20;
    public int MaxRunMinutes { get; set; } = 15;
    public int OutputCapBytes { get; set; } = 4096;
    public int WatchRetrySeconds { get; set; } = 60;
    /// <summary>Upper bound on missed rows written for one routine after a long outage; the rest are summed in one row.</summary>
    public int MaxMissedRows { get; set; } = 100;
}

public sealed class HandoffSettings
{
    public int MaxDepth { get; set; } = 3;
    public int MaxChildrenPerTurn { get; set; } = 4;
    public int MaxLivePerTree { get; set; } = 16;
    public int DedupeWindowMinutes { get; set; } = 10;
    public long TreeTokenBudget { get; set; } = 200_000;
    public int TreeTurnBudget { get; set; } = 30;
    public int TreeDeadlineMinutes { get; set; } = 120;
    public int ConsultMaxTurns { get; set; } = 2;
    public int NotifyMaxBytes { get; set; } = 4096;
    public int BriefMaxChars { get; set; } = 8000;
    public int InputMaxBytes { get; set; } = 16384;
    public int ResultMaxChars { get; set; } = 16000;
    /// <summary>Identical requests of at least this many characters wait for one answer (plan 7.5).</summary>
    public int SameRequestMinChars { get; set; } = 40;
}

public sealed class MemorySettings
{
    public int MaxLines { get; set; } = 200;
    public int MaxBytes { get; set; } = 24576;
    public int TidyHour { get; set; } = 3;
}

public sealed class WebSettings
{
    public bool Enabled { get; set; }
    public string? PublicOrigin { get; set; }
    public string? TlsCertFile { get; set; }
    public string? TlsKeyFile { get; set; }
    public bool BehindHttpsFront { get; set; }
    public string LoginMethod { get; set; } = "password_totp";
    public string? AdminSecretFile { get; set; }
    public int SessionHours { get; set; } = 12;
    public int IdleMinutes { get; set; } = 30;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int AddressFailures { get; set; } = 20;
    public int AddressWindowMinutes { get; set; } = 15;
    /// <summary>Where the running source is offered (AGPL-3.0 section 13); the page links to it in every mode.</summary>
    public string SourceUrl { get; set; } = "https://github.com/StartupHakk/OpenMonoAgent.ai";
}

public sealed class FleetSettings
{
    /// <summary>Builder choice B14: name of the human who turned fleet mode on.</summary>
    public string? EnabledBy { get; set; }
    /// <summary>Enrollment listens here; node N uses ControllerPort + N, each reached through its own relay port.</summary>
    public int ControllerPort { get; set; } = 7490;
    public string? ControllerCertPin { get; set; }
    public int LeaseSeconds { get; set; } = 60;
    public bool KillSwitch { get; set; }
    public int MaxNodes { get; set; } = 24;
    /// <summary>The controller's name in its TLS certificate; nodes reach it through the relay by this name.</summary>
    public string ControllerName { get; set; } = "oma-fleet-controller";
}

public sealed class NotifySettings
{
    /// <summary>Optional local command, off by default (builder choice B3).</summary>
    public string? Command { get; set; }
}

public sealed class ImageSettings
{
    /// <summary>Accepted agent image digest. A change needs `oma-bot images accept`.</summary>
    public string? AcceptedDigest { get; set; }
}
