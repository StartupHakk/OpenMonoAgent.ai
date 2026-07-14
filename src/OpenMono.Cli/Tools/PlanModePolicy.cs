namespace OpenMono.Tools;

/// <summary>
/// Single source of truth for which tools may run while the session is in Plan mode.
///
/// The rule: only read-only tools are allowed in Plan mode. This is enforced server-side
/// in <c>LocalToolExecutor</c> as a HARD gate — independent of what the LLM was told in the
/// system prompt and independent of the tool-definition filtering. The prompt and filtering
/// are advisory (an LLM can hallucinate a tool call for a tool it was never offered); this
/// policy is the actual check-and-balance that cannot be bypassed.
///
/// The same predicate is used to filter the tool definitions sent to the LLM and to build
/// the plan-mode banner, so all three stay consistent by construction.
/// </summary>
public static class PlanModePolicy
{
    /// <summary>True if <paramref name="tool"/> is permitted while in Plan mode.</summary>
    public static bool IsToolAllowed(ITool tool) => tool.IsReadOnly;

    /// <summary>Generic, user-facing message returned when a tool is blocked in Plan mode.</summary>
    public static string BlockedMessage(string toolName) =>
        $"'{toolName}' is not available in Plan mode (read-only). " +
        $"Switch to Build mode using the Plan/Build toggle to make changes, then try again. " +
        $"Do not claim the action was performed.";
}
