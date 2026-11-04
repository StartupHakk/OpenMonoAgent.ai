// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Omabot.Common;
using Omabot.Config;
using Omabot.Team;

namespace Omabot.Render;

/// <summary>Everything the renderer needs that is not in the bot spec.</summary>
public sealed record RenderContext(
    string GatewayUrl,
    bool McpShimInstalled,
    int? ContextSize,
    IReadOnlyList<string> BlockedHosts,
    string MemoryBlock = "(no memory yet)",
    string TopicIndex = "(no topics yet)");

/// <summary>
/// Renders settings.json, system-prompt.md and OPENMONO.md for one bot
/// (plan 7.1 step 3, 7.2). Folders are created first and never overwrite a
/// rendered file (lesson L1). Hard rules live in the data dir system prompt,
/// outside the workspace (lesson L2).
/// </summary>
public sealed class BotRenderer(RosterPaths paths, RosterConfig config)
{
    public RosterConfig Config { get; } = config;

    public const string HooksMount = "/omabot/hooks";
    public const string ShimMount = "/omabot/mcp-shim";
    public const string BotCfgMount = "/omabot/bot";
    public const string SockMount = "/omabot/sock";

    /// <summary>Strings that must never appear in a tool input (self-approval and escape attempts, SEC-03, SEC-21).</summary>
    public static readonly string[] BlockedInputs =
    [
        ":7475", "/api/v1/sessions", "/api/v1/discovery", "supervisor.sock", "roster.sock", "docker.sock",
        "169.254.169.254", "metadata.google.internal", "host.docker.internal", ":7477", ":7476", "/admin/invite",
    ];

    public string WorkspaceOf(BotSpec spec) => spec.Workspace == "auto" ? paths.Bot(spec.Slug).DefaultWorkspace : spec.Workspace;

    /// <summary>Create the bot folders. Only missing folders are created; files are never touched here.</summary>
    public void EnsureFolders(BotSpec spec)
    {
        var bp = paths.Bot(spec.Slug);
        SecureFile.EnsureDirNoClobber(paths.Home);
        SecureFile.EnsureDirNoClobber(paths.Bots);
        SecureFile.EnsureDirNoClobber(paths.Secrets);
        SecureFile.EnsureDirNoClobber(paths.BotSecretsRoot);
        SecureFile.EnsureDirNoClobber(paths.Logs);
        SecureFile.EnsureDirNoClobber(paths.Run);
        SecureFile.EnsureDirNoClobber(bp.Root);
        SecureFile.EnsureDirNoClobber(bp.Data);
        SecureFile.EnsureDirNoClobber(Path.Combine(bp.Data, "memory"));
        SecureFile.EnsureDirNoClobber(Path.Combine(bp.Data, "sessions"));
        SecureFile.EnsureDirNoClobber(bp.Journal);
        SecureFile.EnsureDirNoClobber(bp.SecretsDir);
        SecureFile.EnsureDirNoClobber(Path.Combine(bp.Root, "botcfg"));
        SecureFile.EnsureDirNoClobber(bp.ShimSocketDir);
        if (spec.Workspace == "auto")
        {
            SecureFile.EnsureDirNoClobber(bp.DefaultWorkspace);
        }
        var ws = WorkspaceOf(spec);
        SecureFile.EnsureDirNoClobber(Path.Combine(ws, "memory"));
        SecureFile.EnsureDirNoClobber(Path.Combine(ws, "memory", "log"));
    }

    /// <summary>Folders, then files. Returns the hash of OPENMONO.md as rendered.</summary>
    public RenderedBot Render(BotSpec spec, RenderContext ctx)
    {
        EnsureFolders(spec);
        var bp = paths.Bot(spec.Slug);
        var ws = WorkspaceOf(spec);

        var settings = RenderSettings(spec, ctx);
        SecureFile.WriteAllTextAtomic(bp.Settings, settings);

        var system = RenderSystemPrompt(spec);
        SecureFile.WriteAllTextAtomic(bp.SystemPrompt, system);
        if (spec.PlanPrompt is not null) SecureFile.WriteAllTextAtomic(bp.PlanPrompt, spec.PlanPrompt);

        var omd = RenderOpenMonoMd(spec, ctx);
        SecureFile.WriteAllTextAtomic(Path.Combine(ws, "OPENMONO.md"), omd);
        SecureFile.WriteIfMissing(Path.Combine(ws, "MEMORY.md"), "");

        var botcfg = Path.Combine(bp.Root, "botcfg");
        SecureFile.WriteAllTextAtomic(Path.Combine(botcfg, "allowed-tools.txt"), string.Join("\n", spec.ToolsAllow) + "\n", SecureFile.PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        SecureFile.WriteAllTextAtomic(Path.Combine(botcfg, "blocked-hosts.txt"), string.Join("\n", ctx.BlockedHosts) + "\n", SecureFile.PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        SecureFile.WriteAllTextAtomic(Path.Combine(botcfg, "allow-hosts.txt"), string.Join("\n", spec.AllowHosts) + "\n", SecureFile.PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        SecureFile.WriteAllTextAtomic(bp.BotYaml, spec.ToJson() + "\n");
        return new RenderedBot(settings, system, omd, SecureFile.Sha256Hex(omd));
    }

    /// <summary>Rewrites only OPENMONO.md (memory changes before a turn). Returns its hash.</summary>
    public string WriteOpenMono(BotSpec spec, RenderContext ctx)
    {
        var omd = RenderOpenMonoMd(spec, ctx);
        SecureFile.WriteAllTextAtomic(Path.Combine(WorkspaceOf(spec), "OPENMONO.md"), omd);
        return SecureFile.Sha256Hex(omd);
    }

    public string RenderSystemPrompt(BotSpec spec)
    {
        var identity = $"Your name is {spec.Name}." + (spec.Title is null ? "" : $" Your role: {spec.Title}.")
            + (spec.Team is null ? "" : $" You are on team {spec.Team}.")
            + " Introduce yourself the same way every time.";
        return Templates.Fill(Templates.Get("system-prompt.md.tmpl"), new Dictionary<string, string>
        {
            ["name"] = spec.Name,
            ["identity"] = identity,
            ["personality"] = spec.Personality.TrimEnd(),
            ["hard_rules"] = Templates.Get("hard-rules.md").TrimEnd(),
        }).TrimEnd() + "\n";
    }

    /// <summary>OMA reads at most this much of OPENMONO.md (plan 4.1).</summary>
    public const int OpenMonoMaxLines = 200;
    public const int OpenMonoMaxBytes = 25 * 1024;

    public string RenderOpenMonoMd(BotSpec spec, RenderContext ctx)
    {
        string Fill(string memory, string topics) => Templates.Fill(Templates.Get("OPENMONO.md.tmpl"), new Dictionary<string, string>
        {
            ["name"] = spec.Name,
            ["title_suffix"] = spec.Title is null ? "" : " (" + spec.Title + ")",
            ["instructions"] = string.IsNullOrWhiteSpace(spec.Instructions) ? "(none)" : spec.Instructions!.TrimEnd(),
            ["hard_rules"] = Templates.Get("hard-rules.md").TrimEnd(),
            ["memory_block"] = memory,
            ["topic_index"] = topics,
        }).TrimEnd() + "\n";

        var memory = spec.MemoryEnabled ? ctx.MemoryBlock : "(memory is off for this bot)";
        var topics = spec.MemoryEnabled ? ctx.TopicIndex : "(memory is off for this bot)";
        var text = Fill(memory, topics);
        if (Fits(text)) return text;

        // Over the cap: trim the memory block and topic index from the end so
        // the hard rules and instructions are always inside what OMA reads.
        const string marker = "\n(trimmed to fit the OPENMONO.md cap)";
        var fixedPart = Fill("", "");
        var lineRoom = OpenMonoMaxLines - fixedPart.Split('\n').Length - 2;
        var byteRoom = OpenMonoMaxBytes - System.Text.Encoding.UTF8.GetByteCount(fixedPart) - 2 * System.Text.Encoding.UTF8.GetByteCount(marker) - 16;
        topics = Trim(topics, Math.Max(0, lineRoom / 4), Math.Max(0, byteRoom / 4), marker);
        memory = Trim(memory, Math.Max(0, lineRoom - topics.Split('\n').Length), Math.Max(0, byteRoom - System.Text.Encoding.UTF8.GetByteCount(topics)), marker);
        text = Fill(memory, topics);
        return Fits(text) ? text : Fill("(memory omitted: instructions use the whole OPENMONO.md cap)", "");
    }

    private static bool Fits(string text)
        => text.Split('\n').Length <= OpenMonoMaxLines && System.Text.Encoding.UTF8.GetByteCount(text) <= OpenMonoMaxBytes;

    private static string Trim(string block, int maxLines, int maxBytes, string marker)
    {
        if (block.Split('\n').Length <= maxLines && System.Text.Encoding.UTF8.GetByteCount(block) <= maxBytes) return block;
        var sb = new System.Text.StringBuilder();
        var lines = 0;
        foreach (var line in block.Split('\n'))
        {
            var piece = (sb.Length > 0 ? "\n" : "") + line;
            if (lines + 1 >= maxLines || System.Text.Encoding.UTF8.GetByteCount(sb.ToString() + piece) > maxBytes) break;
            sb.Append(piece);
            lines++;
        }
        return sb.ToString() + marker;
    }

    public string RenderSettings(BotSpec spec, RenderContext ctx)
    {
        var root = new JsonObject
        {
            ["llm"] = new JsonObject
            {
                ["endpoint"] = ctx.GatewayUrl,
                ["model"] = spec.Model,
            },
            ["web"] = new JsonObject
            {
                ["search"] = spec.WebSearch && spec.ToolsAllow.Contains("WebSearch"),
                ["scrape"] = spec.WebScrape && spec.ToolsAllow.Contains("WebFetch"),
            },
            ["permissions"] = new JsonObject { ["tools"] = PermissionProfiles.Build(spec) },
            ["hooks"] = RenderHooks(),
            ["agents"] = new JsonObject
            {
                ["max_concurrent_agents"] = 1,
                ["max_nesting_depth"] = 3,
            },
            ["auto_detect_code_graph"] = false,
        };
        if (ctx.ContextSize is { } cs) ((JsonObject)root["llm"]!)["context_size"] = cs;
        if (ctx.McpShimInstalled)
        {
            root["mcp_servers"] = new JsonObject
            {
                ["roster"] = new JsonObject
                {
                    ["command"] = ShimMount + "/run",
                    ["args"] = new JsonArray(),
                    ["env"] = new JsonObject { ["ROSTER_BOT"] = spec.Slug, ["ROSTER_SOCKET"] = SockMount + "/roster.sock" },
                    ["enabled"] = true,
                },
            };
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// Hooks are bash only (L4) and never pass {{tool_input}} or {{tool_output}},
    /// because OMA substitutes them without quoting (builder choice B10).
    /// </summary>
    public static JsonObject RenderHooks()
    {
        var pre = new JsonArray
        {
            new JsonObject { ["run"] = HooksMount + "/pre-tool.sh '{{tool_name}}'" },
        };
        foreach (var blocked in BlockedInputs)
        {
            pre.Add(new JsonObject
            {
                ["if"] = new JsonObject { ["input_contains"] = blocked },
                ["run"] = HooksMount + "/block.sh blocked_input",
            });
        }
        var post = new JsonArray
        {
            new JsonObject { ["if"] = new JsonObject { ["tool"] = "FileWrite" }, ["run"] = HooksMount + "/post-tool.sh '{{tool_name}}'" },
            new JsonObject { ["if"] = new JsonObject { ["tool"] = "FileEdit" }, ["run"] = HooksMount + "/post-tool.sh '{{tool_name}}'" },
            new JsonObject { ["if"] = new JsonObject { ["tool"] = "ApplyPatch" }, ["run"] = HooksMount + "/post-tool.sh '{{tool_name}}'" },
        };
        return new JsonObject
        {
            ["pre_tool_use"] = pre,
            ["post_tool_use"] = post,
            ["session_start"] = new JsonArray(),
        };
    }
}

public sealed record RenderedBot(string Settings, string SystemPrompt, string OpenMonoMd, string OpenMonoHash);
