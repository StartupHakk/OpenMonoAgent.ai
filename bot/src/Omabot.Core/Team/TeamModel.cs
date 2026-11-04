// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Team;

/// <summary>One problem in a team file: the field path, the line and a plain message.</summary>
public sealed record TeamError(string Path, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{Path} (line {Line}): {Message}" : $"{Path}: {Message}";
}

public sealed class TeamFileException(IReadOnlyList<TeamError> errors)
    : Exception(string.Join("\n", errors.Select(e => e.ToString())))
{
    public IReadOnlyList<TeamError> Errors { get; } = errors;
}

/// <summary>A text field that came either inline or from a file, already read with LF line ends.</summary>
public sealed record TextValue(string Text, string? SourceFile, int Line);

/// <summary>Reference to a secret: only a file or an env name is ever allowed.</summary>
public sealed record SecretRef(string Kind, string Value)
{
    public override string ToString() => Kind + ":" + Value;
}

public sealed class MemoryDef
{
    public bool? Enabled { get; set; }
    public int? MaxLines { get; set; }
    public int? MaxBytes { get; set; }
    public bool? Capture { get; set; }
    public bool? Tidy { get; set; }
    public bool? Recall { get; set; }
}

public sealed class LimitsDef
{
    public string? Memory { get; set; }
    public double? Cpu { get; set; }
    public int? MaxTurnMinutes { get; set; }
    public long? DailyTokenBudget { get; set; }
    public int? Pids { get; set; }
}

public sealed class WebDef
{
    public bool? Search { get; set; }
    public bool? Scrape { get; set; }
    public List<string>? AllowHosts { get; set; }
}

public sealed class CheckDef
{
    public string Kind { get; set; } = "always";
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RoutineDef
{
    public string Name { get; set; } = "";
    public string? Was { get; set; }
    public string? Bot { get; set; }
    public string? Cron { get; set; }
    public int? IntervalMinutes { get; set; }
    public string? At { get; set; }
    public string? Watch { get; set; }
    public string? Timezone { get; set; }
    public List<CheckDef> Checks { get; set; } = [];
    public string Instructions { get; set; } = "";
    public string Notify { get; set; } = "errors_only";
    public long? DailyTokenBudget { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllowExternal { get; set; }
    public int Line { get; set; }
}

public sealed class BotDef
{
    public int Line { get; set; }
    public string Name { get; set; } = "";
    public string? Was { get; set; }
    public string? Team { get; set; }
    public string? Role { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? SeenAs { get; set; }
    public TextValue? Personality { get; set; }
    public TextValue? PlanPrompt { get; set; }
    public TextValue? Instructions { get; set; }
    public string? Model { get; set; }
    public string? PermissionProfile { get; set; }
    public List<string>? ToolsAllow { get; set; }
    public List<string>? ToolsDeny { get; set; }
    public List<string>? BashAllow { get; set; }
    public List<string>? BashDeny { get; set; }
    public string? Workspace { get; set; }
    public int WorkspaceLine { get; set; }
    public bool? SharedWorkspace { get; set; }
    public bool? HostExec { get; set; }
    public string? Desktop { get; set; }
    public WebDef? Web { get; set; }
    public MemoryDef? Memory { get; set; }
    public string? Timezone { get; set; }
    public int? IdleSleepMinutes { get; set; }
    public LimitsDef? Limits { get; set; }
    public List<RoutineDef>? Routines { get; set; }
    public List<string>? Rooms { get; set; }
    public List<string>? AcceptsFrom { get; set; }
    public List<string>? Manages { get; set; }
    public List<string>? Keywords { get; set; }
    public Dictionary<string, SecretRef>? Secrets { get; set; }
}

public sealed class DefaultsDef
{
    public string? Model { get; set; }
    public string? PermissionProfile { get; set; }
    public string? Timezone { get; set; }
    public int? IdleSleepMinutes { get; set; }
    public MemoryDef? Memory { get; set; }
    public LimitsDef? Limits { get; set; }
    public bool? HostExec { get; set; }
    public string? Desktop { get; set; }
}

public sealed class ModelDef
{
    public string Nickname { get; set; } = "";
    public string UpstreamModel { get; set; } = "";
    public int? ContextSize { get; set; }
    public string? Backend { get; set; }
    public List<string> Labels { get; set; } = [];
}

public sealed class TeamDef
{
    public string Name { get; set; } = "";
    public string? Lead { get; set; }
    public TextValue? Brief { get; set; }
    public int Line { get; set; }
}

public sealed class RouteDef
{
    public List<string> Words { get; set; } = [];
    public string To { get; set; } = "";
}

public sealed class RoomDef
{
    public string Name { get; set; } = "";
    public string? Team { get; set; }
    public List<string> Members { get; set; } = [];
    public string Responder { get; set; } = "lead";
    public TextValue? Brief { get; set; }
    public int MaxPostsPerHour { get; set; } = 30;
    public int Line { get; set; }
}

/// <summary>A parsed team file before it is checked against live state.</summary>
public sealed class TeamFile
{
    public int Version { get; set; }
    public string BaseDir { get; set; } = ".";
    public DefaultsDef Defaults { get; set; } = new();
    public List<ModelDef> Models { get; set; } = [];
    public List<TeamDef> Teams { get; set; } = [];
    public List<BotDef> Bots { get; set; } = [];
    public List<RoutineDef> Routines { get; set; } = [];
    public List<RouteDef> Routes { get; set; } = [];
    public List<RoomDef> Rooms { get; set; } = [];
    public List<string> BlockedHosts { get; set; } = [];
}
