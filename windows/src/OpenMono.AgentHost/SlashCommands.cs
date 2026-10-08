namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Slash command palette model for the native chat window. Mirrors the OMA
/// slash commands (help, model, status, compact, plan, think, and so on) as
/// palette entries with descriptions; execution routes through the normal
/// OMA command registry.
/// </summary>
public static class SlashCommands
{
    public sealed record Entry(string Command, string Description);

    public static readonly IReadOnlyList<Entry> All =
    [
        new Entry("/help", "Show available commands"),
        new Entry("/model", "Show or switch the active model"),
        new Entry("/status", "Show inference server and session status"),
        new Entry("/compact", "Compact the conversation to save context"),
        new Entry("/checkpoint", "Save a session checkpoint"),
        new Entry("/plan", "Enter plan mode for a task"),
        new Entry("/think", "Toggle thinking display"),
        new Entry("/mode", "Switch agent mode"),
        new Entry("/playbook", "Run a saved playbook"),
        new Entry("/prompt", "Show the active system prompt"),
        new Entry("/export", "Export the session transcript"),
        new Entry("/clear", "Clear the conversation view"),
        new Entry("/undo", "Undo the last file change"),
        new Entry("/retry", "Retry the last turn"),
        new Entry("/stats", "Show session token statistics"),
        new Entry("/debug", "Toggle verbose diagnostics"),
        new Entry("/init", "Initialize project configuration"),
        new Entry("/resume", "Resume a previous session"),
        new Entry("/btw", "Note something for this session"),
    ];

    public static bool IsSlashCommand(string text) =>
        text.TrimStart().StartsWith("/", StringComparison.Ordinal);

    public static IReadOnlyList<Entry> Filter(string prefix)
    {
        var query = prefix.TrimStart();
        if (!query.StartsWith("/", StringComparison.Ordinal))
        {
            query = "/" + query;
        }

        return All
            .Where(e => e.Command.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
