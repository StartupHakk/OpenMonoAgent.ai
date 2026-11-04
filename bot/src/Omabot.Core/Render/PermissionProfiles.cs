// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Omabot.Team;

namespace Omabot.Render;

/// <summary>
/// Turns a permission profile (ask, edits, auto) and the bot's tool lists into
/// OMA `permissions.tools` rules. Tools the bot is not given are denied
/// outright. Some writes are always denied, and some tools always ask.
/// </summary>
public static class PermissionProfiles
{
    /// <summary>File patterns no bot may write with file tools.</summary>
    public static readonly string[] ProtectedFiles =
    [
        "*OPENMONO.md", "*/.openmono/*", ".openmono/*", "*.env", "*.pem", "*.key", "*id_rsa*", "*id_ed25519*",
        "*MEMORY.md", "*/memory/*.md", "memory/*.md",
    ];

    public static readonly string[] AlwaysDenyBash =
    [
        "*:7475*", "*/api/v1/*", "*supervisor.sock*", "*roster.sock*", "*docker.sock*", "*169.254.169.254*",
        "docker *", "*host.docker.internal*", "sudo *", "*:7477*", "*/admin/invite*",
    ];

    public static JsonObject Build(BotSpec spec)
    {
        var profile = JsonDocument.Parse(Templates.Get($"profiles/{spec.PermissionProfile}.json")).RootElement;
        var write = profile.GetProperty("write").GetString();
        var tools = new JsonObject();
        var allowed = new HashSet<string>(spec.ToolsAllow, StringComparer.Ordinal);
        foreach (var d in spec.ToolsDeny) allowed.Remove(d);

        foreach (var tool in ToolCatalog.OmaTools)
        {
            if (!allowed.Contains(tool))
            {
                tools[tool] = Rules(deny: ["*"]);
                continue;
            }
            switch (tool)
            {
                case "Bash":
                    {
                        var deny = AlwaysDenyBash.Concat(spec.BashDeny).Distinct().ToList();
                        var allow = spec.PermissionProfile == "ask" ? [] : spec.BashAllow;
                        tools[tool] = Rules(allow: allow, deny: deny, ask: ["*"]);
                        break;
                    }
                case "HostExec":
                    tools[tool] = Rules(ask: ["*"]);
                    break;
                case "FileWrite" or "FileEdit" or "ApplyPatch":
                    tools[tool] = write == "allow"
                        ? Rules(allow: ["*"], deny: [.. ProtectedFiles])
                        : Rules(deny: [.. ProtectedFiles], ask: ["*"]);
                    break;
                default:
                    if (ToolCatalog.IsReadOnly(tool)) tools[tool] = Rules(allow: ["*"]);
                    else if (write != "allow") tools[tool] = Rules(ask: ["*"]);
                    else tools[tool] = Rules(allow: ["*"]);
                    break;
            }
        }
        foreach (var name in ToolCatalog.RosterTools)
        {
            var full = "mcp__roster__" + name;
            tools[full] = allowed.Contains(full) ? Rules(allow: ["*"]) : Rules(deny: ["*"]);
        }
        return tools;
    }

    private static JsonObject Rules(IEnumerable<string>? allow = null, IEnumerable<string>? deny = null, IEnumerable<string>? ask = null)
    {
        static JsonArray Arr(IEnumerable<string>? xs) => [.. (xs ?? []).Select(x => (JsonNode)JsonValue.Create(x)!)];
        return new JsonObject { ["allow"] = Arr(allow), ["deny"] = Arr(deny), ["ask"] = Arr(ask) };
    }
}
