namespace OpenMono.Session;

public sealed class TokenTracker
{
    public int TotalPromptTokens { get; private set; }
    public int TotalCompletionTokens { get; private set; }
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;
    public int ApiCalls { get; private set; }
    public int MaxPromptTokens { get; private set; }
    public int AvgPromptTokens => ApiCalls > 0 ? TotalPromptTokens / ApiCalls : 0;
    public Dictionary<string, int> ToolUsageCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int FilesModified { get; set; }
    public int FilesCreated { get; set; }

    public int LastPromptTokens { get; private set; }

    public Action<int, int>? OnUsageUpdated { get; set; }

    public void RecordUsage(int promptTokens, int completionTokens)
    {
        TotalPromptTokens += promptTokens;
        TotalCompletionTokens += completionTokens;
        LastPromptTokens = promptTokens;
        if (promptTokens > MaxPromptTokens) MaxPromptTokens = promptTokens;
        ApiCalls++;
        OnUsageUpdated?.Invoke(TotalPromptTokens, TotalCompletionTokens);
    }

    public void RecordToolUse(string toolName)
    {
        ToolUsageCounts.TryGetValue(toolName, out var count);
        ToolUsageCounts[toolName] = count + 1;
    }

    public string GetSummary(DateTime sessionStart)
    {
        var elapsed = DateTime.UtcNow - sessionStart;
        var lines = new List<string>
        {
            "Session Statistics",
            "══════════════════",
            $"  Duration:          {elapsed:hh\\:mm\\:ss}",
            $"  API calls:         {ApiCalls}",
            $"  Prompt tokens:     {TotalPromptTokens:N0}",
            $"  Completion tokens: {TotalCompletionTokens:N0}",
            $"  Total tokens:      {TotalTokens:N0}",
        };

        if (FilesModified > 0 || FilesCreated > 0)
        {
            lines.Add($"  Files created:     {FilesCreated}");
            lines.Add($"  Files modified:    {FilesModified}");
        }

        if (ToolUsageCounts.Count > 0)
        {
            lines.Add("");
            lines.Add("Tool Usage");
            lines.Add("──────────");
            foreach (var (tool, count) in ToolUsageCounts.OrderByDescending(kv => kv.Value))
                lines.Add($"  {tool,-20} {count,4}x");
        }

        return string.Join('\n', lines);
    }
}
