using OpenMono.Config;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Windows default permission pack. Written to the user settings on first run
/// and merged with user rules (never replacing them). Registered through the
/// standard AppConfig surface so no OMA change is needed.
/// </summary>
public static class WindowsPermissionDefaults
{
    public static void Apply(AppConfig config)
    {
        EnsureTool(config, "Bash",
            allow:
            [
                "git *",
                "dotnet *",
                "npm *",
                "rg *",
                "Get-ChildItem *",
                "Get-Content *",
            ],
            deny:
            [
                "format *",
                "diskpart*",
                "bcdedit*",
                "reg delete*",
                "Remove-Item -Recurse C:\\*",
                "del /s /q C:\\Windows*",
                "rmdir /s /q *",
                "*.env",
                "*.pem",
            ],
            ask:
            [
                "*runas*",
                "*-Verb RunAs*",
                "reg add*",
                "sc *",
                "schtasks*",
                "* -EncodedCommand*",
            ]);
    }

    public static Dictionary<string, ToolPermissionRules> Defaults() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bash"] = new ToolPermissionRules
            {
                Allow = ["git *", "dotnet *", "npm *", "rg *", "Get-ChildItem *", "Get-Content *"],
                Deny = ["format *", "diskpart*", "bcdedit*", "reg delete*", "Remove-Item -Recurse C:\\*", "del /s /q C:\\Windows*", "rmdir /s /q *", "*.env", "*.pem"],
                Ask = ["*runas*", "*-Verb RunAs*", "reg add*", "sc *", "schtasks*", "* -EncodedCommand*"],
            },
        };

    private static void EnsureTool(AppConfig config, string tool, string[] allow, string[] deny, string[] ask)
    {
        if (!config.Permissions.Tools.TryGetValue(tool, out var rules))
        {
            rules = new ToolPermissionRules();
            config.Permissions.Tools[tool] = rules;
        }

        Merge(rules.Allow, allow);
        Merge(rules.Deny, deny);
        Merge(rules.Ask, ask);
    }

    private static void Merge(List<string> target, string[] values)
    {
        foreach (var value in values)
        {
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }
    }
}
