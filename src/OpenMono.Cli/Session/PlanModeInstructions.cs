namespace OpenMono.Session;

internal static class PlanModeInstructions
{
    internal static string Activation(string reason) =>
        $"Plan mode activated: {reason}\n\n" +
        "IMPORTANT: You cannot create, write, or edit anything right now.\n" +
        "Do NOT say 'I'll create X' or 'I'll implement X'. You are not implementing.\n" +
        "Your only deliverable is a written plan document. Exit plan mode to implement.\n\n" +
        "--- What to do ---\n\n" +
        "Step 1 — Investigate (start here)\n" +
        "Use TodoWrite to list what you need to understand, then work through it:\n" +
        "  FileRead / Grep / Glob / Roslyn — read code, trace calls, find references\n" +
        "  Lsp / ListDirectory — navigate structure\n" +
        "  WebFetch / WebSearch — external references if needed\n" +
        "Mark each item done as you go. Do not skip this — a plan without investigation is guessing.\n\n" +
        "Step 2 — Clarify ONLY if genuinely stuck\n" +
        "If and ONLY IF the core implementation approach is still unclear after investigating,\n" +
        "use AskUser for one focused question. Do not ask if the user already gave specific instructions.\n\n" +
        "Step 3 — Write the plan\n" +
        "Produce a numbered implementation plan with:\n" +
        "  1. One-sentence summary of the chosen approach\n" +
        "  2. Every file that changes and exactly what changes in each\n" +
        "  3. For each step, the tool you will use and why it fits (e.g. Context7 for\n" +
        "     external API docs, Grep to locate usages, FileEdit for the change itself)\n" +
        "  4. Any risks, edge cases, or decisions the user needs to make\n" +
        "  5. Complexity: trivial / moderate / large\n" +
        "Be specific. Someone else should be able to implement from your plan alone.\n\n" +
        "Step 4 — Call ExitPlanMode with the full plan as the `plan` argument.\n" +
        "Only after that may you write any code.";

    internal const string Deactivation =
        "Plan mode has been deactivated by the user (/plan). You may now write and edit files.";

    internal const string PlanPresented =
        "The plan above has been presented to the user and plan mode has ended.\n" +
        "- If the user approves the plan → implement it now.\n" +
        "- If the user asks for changes, a different plan, or says 'no' → call EnterPlanMode first, then revise and present a new plan before writing any code.\n" +
        "Do NOT call ExitPlanMode again without first calling EnterPlanMode — plan mode is currently off.";
}
