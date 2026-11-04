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
    /// <summary>Docker bridge gateway address. Never loopback.</summary>
    public string BindAddress { get; set; } = "172.17.0.1";
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
    /// <summary>Builder choice B7: empty, Spencer approves everything.</summary>
    public List<ApproverBot> ApproverBots { get; set; } = [];
    public int PauseTimeoutMinutes { get; set; } = 10;
    public int ApprovalTtlMinutes { get; set; } = 15;
    public List<string> AlwaysAsk { get; set; } = ["HostExec", "Bash", "FileWrite", "ApplyPatch"];
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
    public string Network { get; set; } = "oma-roster";
    public int ContainerUid { get; set; } = 1000;
}

public sealed class DesktopSettings
{
    public int PortBase { get; set; } = 7600;
    public int Max { get; set; } = 8;
    public string Image { get; set; } = "oma-roster-desktop:latest";
    public int LeaseMinutes { get; set; } = 30;
}

public sealed class EgressSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 3128;
    public string Host { get; set; } = "egress";
    public List<string> BlockedHosts { get; set; } = ["x.com", "twitter.com", "facebook.com", "fb.com", "instagram.com"];
}

public sealed class RoutineSettings
{
    public int MinIntervalMinutes { get; set; } = 5;
    public double SlotShare { get; set; } = 0.5;
    public int CatchUpHours { get; set; } = 12;
    public int MaxJitterSeconds { get; set; } = 60;
}

public sealed class HandoffSettings
{
    public int MaxDepth { get; set; } = 3;
    public int MaxChildrenPerTurn { get; set; } = 4;
    public int MaxLivePerTree { get; set; } = 16;
    public int DedupeWindowMinutes { get; set; } = 10;
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
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

public sealed class FleetSettings
{
    /// <summary>Builder choice B14: name of the human who turned fleet mode on.</summary>
    public string? EnabledBy { get; set; }
    public int ControllerPort { get; set; } = 7490;
    public string? ControllerCertPin { get; set; }
    public int LeaseSeconds { get; set; } = 60;
    public bool KillSwitch { get; set; }
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
