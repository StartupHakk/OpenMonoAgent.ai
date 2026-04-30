using System.Text.Json;
using OpenMono.Config;
using OpenMono.Session;

namespace OpenMono.Tui.Export;

public static class JsonExporter
{
    public static string Export(SessionState session)
    {
        var export = new
        {
            session.Id,
            session.StartedAt,
            session.TurnCount,
            session.TotalTokensUsed,
            Messages = session.Messages.Select(m => new
            {
                m.Role,
                m.Content,
                m.ToolCalls,
                m.ToolCallId,
                m.ToolName,
                m.Timestamp
            })
        };

        return JsonSerializer.Serialize(export, JsonOptions.Indented);
    }
}
