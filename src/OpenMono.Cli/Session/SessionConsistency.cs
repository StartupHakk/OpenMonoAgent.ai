namespace OpenMono.Session;

public static class SessionConsistency
{
    public const string InterruptedToolResult =
        "Interrupted before completion. Re-issue the tool call to execute.";

    public static int Repair(SessionState session)
    {
        var (repaired, synthesized) = RepairMessages(session.Messages);
        if (synthesized > 0)
        {
            session.Messages.Clear();
            session.Messages.AddRange(repaired);
        }
        return synthesized;
    }

    public static (List<Message> Messages, int Synthesized) RepairMessages(IReadOnlyList<Message> messages)
    {
        var answered = new HashSet<string>(
            messages
                .Where(m => m.Role == MessageRole.Tool && m.ToolCallId is not null)
                .Select(m => m.ToolCallId!));

        var result = new List<Message>(messages.Count);
        var synthesized = 0;

        foreach (var message in messages)
        {
            result.Add(message);
            if (message.Role != MessageRole.Assistant || message.ToolCalls is not { Count: > 0 } calls)
                continue;

            foreach (var call in calls)
            {
                if (!answered.Add(call.Id))
                    continue;

                result.Add(new Message
                {
                    Role = MessageRole.Tool,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Content = InterruptedToolResult,
                });
                synthesized++;
            }
        }

        return (result, synthesized);
    }
}
