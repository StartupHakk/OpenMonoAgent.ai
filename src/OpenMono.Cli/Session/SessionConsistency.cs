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
        synthesized += RepairOrphanPlans(session);
        return synthesized;
    }

    /// <summary>
    /// A cold-loaded session has no live turns — so any plan still marked
    /// Implementing belongs to a process that died mid-implementation. Mark it
    /// Abandoned so UIs stop presenting it as live work (mirrors what
    /// RepairMessages does for unanswered tool calls).
    /// </summary>
    private static int RepairOrphanPlans(SessionState session)
    {
        var abandoned = 0;
        foreach (var plan in session.Meta.Plans)
        {
            if (plan.Status != PlanStatus.Implementing) continue;
            plan.Status = PlanStatus.Abandoned;
            abandoned++;
        }
        return abandoned;
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
