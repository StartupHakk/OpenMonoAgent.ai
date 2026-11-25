namespace OpenMono.Utils;

public static class ThinkingLevels
{
    public static int Resolve(string arg, string[] levels)
    {
        var lower = arg.Trim().ToLowerInvariant();
        return lower switch
        {
            "off" or "0" => Array.IndexOf(levels, "off"),
            "low" or "l" => Array.IndexOf(levels, "low"),
            "medium" or "med" or "m" => Array.IndexOf(levels, "medium"),
            "xhigh" or "high" or "xh" or "h" => Array.IndexOf(levels, "xhigh"),
            _ => -1,
        };
    }

    public static string Describe(string level) => level switch
    {
        "off" => "OFF — fast direct responses",
        "low" => "LOW — brief reasoning, optimized for speed",
        "medium" => "MEDIUM — balanced accuracy and speed (recommended)",
        "xhigh" => "XHIGH — thorough analysis for complex tasks (over-thinks simple tasks)",
        _ => level.ToUpperInvariant(),
    };
}
