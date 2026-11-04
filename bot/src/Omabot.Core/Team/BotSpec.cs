// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Omabot.Common;

namespace Omabot.Team;

/// <summary>
/// A bot definition after defaults are applied, as stored in roster.db.
/// The spec hash is the SHA-256 of the canonical JSON.
/// </summary>
public sealed class BotSpec
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Team { get; set; }
    /// <summary>worker, lead (head-honcho) or health (read only).</summary>
    public string Role { get; set; } = "worker";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? SeenAs { get; set; }
    public string Personality { get; set; } = "";
    public string? PlanPrompt { get; set; }
    public string? Instructions { get; set; }
    public string Model { get; set; } = "";
    public string PermissionProfile { get; set; } = "ask";
    public List<string> ToolsAllow { get; set; } = [];
    public List<string> ToolsDeny { get; set; } = [];
    public List<string> BashAllow { get; set; } = [];
    public List<string> BashDeny { get; set; } = [];
    /// <summary>auto, or an absolute path under an allowed root.</summary>
    public string Workspace { get; set; } = "auto";
    public bool SharedWorkspace { get; set; }
    public bool HostExec { get; set; }
    public string Desktop { get; set; } = "off";
    public bool WebSearch { get; set; }
    public bool WebScrape { get; set; }
    public List<string> AllowHosts { get; set; } = [];
    public bool MemoryEnabled { get; set; } = true;
    public int MemoryMaxLines { get; set; } = 200;
    public int MemoryMaxBytes { get; set; } = 24576;
    public bool MemoryCapture { get; set; } = true;
    public bool MemoryTidy { get; set; } = true;
    public bool Recall { get; set; } = true;
    public string Timezone { get; set; } = "America/Phoenix";
    public int IdleSleepMinutes { get; set; } = 60;
    public string LimitMemory { get; set; } = "1536m";
    public double LimitCpu { get; set; } = 1.0;
    public int LimitPids { get; set; } = 512;
    public int MaxTurnMinutes { get; set; } = 30;
    public long DailyTokenBudget { get; set; } = 400000;
    public List<RoutineSpec> Routines { get; set; } = [];
    public List<string> Rooms { get; set; } = [];
    public List<string> AcceptsFrom { get; set; } = ["person"];
    public List<string> Manages { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    /// <summary>Secret name to "file:PATH" or "env:NAME". Never a value.</summary>
    public SortedDictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
    /// <summary>A sampling preset from bots.presets in roster.json. Null when unset, so older spec hashes stay the same.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preset { get; set; }

    /// <summary>Team file auto rules the approval broker may answer at once ("Tool" or "Tool: literal"). Null when unset, so older spec hashes stay the same.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AutoAllow { get; set; }

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJson);

    public static BotSpec FromJson(string json) => JsonSerializer.Deserialize<BotSpec>(json, CanonicalJson)
        ?? throw new InvalidDataException("Stored bot spec is empty.");

    public string Hash() => SecureFile.Sha256Hex(ToJson());

    /// <summary>Hash of what the container depends on. Routines and broker auto rules are left out, so changing them never restarts a bot.</summary>
    public string ContainerHash()
    {
        var c = Clone();
        c.Routines = [];
        c.AutoAllow = null;
        return c.Hash();
    }

    public BotSpec Clone() => FromJson(ToJson());

    public bool IsReadOnlyRole => Role == "health";
}

public sealed class RoutineSpec
{
    public string Name { get; set; } = "";
    public string? Cron { get; set; }
    public int? IntervalMinutes { get; set; }
    public string? At { get; set; }
    public string? Watch { get; set; }
    public string Timezone { get; set; } = "America/Phoenix";
    public List<CheckDef> Checks { get; set; } = [];
    public string Instructions { get; set; } = "";
    public string Notify { get; set; } = "errors_only";
    public long DailyTokenBudget { get; set; } = 50000;
    public bool Enabled { get; set; } = true;
    public bool AllowExternal { get; set; }
    /// <summary>fresh (a new ACP session per run, the default) or standing (the bot's standing session).</summary>
    public string Session { get; set; } = "fresh";
    /// <summary>off (HostExec is denied in runs of this routine) or ask (every HostExec asks a person).</summary>
    public string HostExec { get; set; } = "off";

    public string Describe()
    {
        if (Cron is not null) return $"{Cron} {Timezone}";
        if (IntervalMinutes is not null) return $"every {IntervalMinutes} min";
        if (At is not null) return $"once at {At} {Timezone}";
        return $"watch {Watch}";
    }
}
