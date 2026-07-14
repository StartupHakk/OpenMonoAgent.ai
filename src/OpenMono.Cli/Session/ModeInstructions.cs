namespace OpenMono.Session;

/// <summary>
/// All prompt text about Plan vs Build mode, one entry per moment it's used. The hard gate in
/// LocalToolExecutor enforces the rules regardless of what the model was told — this text only
/// shapes how the model behaves and explains. The members map to the lifecycle:
///
///   • CurrentModeBanner  — EVERY turn: prepended to the system prompt; the authoritative
///                          statement of the current mode (the single source of mode state).
///   • Activation         — when the agent calls EnterPlanMode: the "how to plan" workflow.
///   • ProceedOptions     — in a CreatePlan presentation: the user's 3 choices, as text.
///   • PlanPresented      — after CreatePlan: tells the agent to wait for the user's choice.
///   • ResolvePlanDecision— maps that choice (auto/ask/keep) to its effect (shared by both UIs).
///   • SwitchedToPlan/Build— one-time notice injected when the USER toggles mode mid-session.
/// </summary>
internal static class ModeInstructions
{
    // ── EVERY turn: authoritative current-mode banner ───────────────────────────────────────
    /// <summary>
    /// PREPENDED to the system message every turn, stating the CURRENT mode authoritatively.
    /// This is the first thing the model reads. Plan and Build each get a banner so the model
    /// never has to infer its mode (and never parrots a stale "I'm in plan mode" from history).
    /// </summary>
    internal static string CurrentModeBanner(bool planMode, IReadOnlyList<string> readOnlyTools)
        => planMode ? PlanBanner(readOnlyTools) : BuildBanner;

    private static string PlanBanner(IReadOnlyList<string> readOnlyTools)
    {
        var tools = readOnlyTools.Count > 0 ? string.Join(", ", readOnlyTools) : "(none)";
        return
            "⚠ ACTIVE MODE: PLAN (read-only). Plan mode is the DEFAULT starting mode — this is normal and intended, " +
            "NOT a system error, state bug, or something that \"shouldn't be\". Do NOT say plan mode shouldn't be active.\n" +
            $"Tools you CAN use right now (read-only): {tools}.\n" +
            "Tools UNAVAILABLE until Build mode: FileWrite, FileEdit, ApplyPatch, Bash (and other write/exec tools).\n" +
            "You can fully investigate, read, search, analyze, research, answer questions, and show example content INLINE in your reply.\n" +
            "\n" +
            "CRITICAL — when the request needs a write/edit/command (e.g. \"save this to a file\", \"create X\", \"run Y\"):\n" +
            "  • Recognize this IMMEDIATELY, on the FIRST turn. Do NOT explore first.\n" +
            "  • Do NOT call FileRead, Glob, Grep, or any read-only tool to attempt, stage, verify, or work around the write — " +
            "that cannot create the file and only wastes turns.\n" +
            "  • Do NOT ask the user where to save it — you cannot save anything in Plan mode.\n" +
            "  • Instead: show the content inline if useful, then say you are in Plan mode (read-only) and ask the user to " +
            "switch to Build mode with the Plan/Build toggle. NEVER claim you made a change you could not make.\n" +
            "─────────────────────────────────────────────────────────────\n\n";
    }

    // Build-mode banner. Without it a weak model parrots its own earlier "I'm in Plan mode"
    // messages still in history even after the user switched — so Build must speak too.
    private const string BuildBanner =
        "✅ ACTIVE MODE: BUILD — you have FULL tool access RIGHT NOW, including FileWrite, FileEdit, ApplyPatch, and Bash.\n" +
        "Disregard any earlier message (including your own) that said you were in Plan mode — that NO LONGER applies.\n" +
        "If the user asks you to create, write, edit, or run something, DO IT NOW with the appropriate tool. " +
        "Do NOT say you are in Plan mode and do NOT ask the user to switch modes.\n" +
        "─────────────────────────────────────────────────────────────\n\n";

    // ── CreatePlan: the user's 3 choices, shown as text (extension also renders them as buttons) ──
    /// <summary>
    /// Human-facing "how to proceed" options, included in every plan presentation so the
    /// choice is always visible in the output itself — independent of the extension's buttons.
    /// </summary>
    internal const string ProceedOptions =
        "**How would you like to proceed?**\n" +
        "1. **Auto implement** — switch to Build mode and implement the plan now.\n" +
        "2. **Ask before edits** — implement the plan, but prompt before each change.\n" +
        "3. **Keep planning** — refine the plan before implementing.";

    // ── Plan decision routing (auto / ask / keep → effect) ─────────────────────────────────
    /// <summary>
    /// Maps a user's plan decision to its effect. Shared by both frontends (extension's
    /// plan_decision turn and the TUI menu) so routing is identical and deterministic.
    /// Returns: whether to implement (flip to Build), whether writes are auto-approved,
    /// and the instruction to drive the implementation turn. "keep"/unknown → don't implement.
    /// </summary>
    internal static (bool Implement, bool AutoApprove, string Instruction) ResolvePlanDecision(string decision)
    {
        if (decision.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return (true, true, "I approve the plan. Implement it now.");
        if (decision.Equals("gated", StringComparison.OrdinalIgnoreCase) ||
            decision.Equals("ask", StringComparison.OrdinalIgnoreCase))
            return (true, false, "I approve the plan. Implement it now — prompt me before each edit.");
        return (false, false, ""); // "keep" / anything else → stay in Plan mode
    }

    // ── EnterPlanMode tool result: the "how to plan" workflow ──────────────────────────────
    /// <summary>Tool-result text when the LLM calls EnterPlanMode.</summary>
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
        "Step 4 — Call CreatePlan with the full plan as the `plan` argument to present it.\n" +
        "You stay in Plan mode. The user reviews it; once they approve, call ImplementPlan to\n" +
        "switch to Build mode and implement. Do not write any code before ImplementPlan.";

    // ── User toggled mode: one-time notice so the model registers the CHANGE ────────────────
    // (The static per-turn banner alone doesn't grab a weak model mid-task.)
    internal const string SwitchedToPlan =
        "[The user just switched to PLAN mode (read-only). You can no longer create, edit, or run anything. " +
        "If you were about to make a change, STOP — do not call read-only tools to work around it. Tell the user " +
        "you are now in Plan mode and they should switch back to Build mode to make changes. You may still read, " +
        "search, and analyze.]";

    internal const string SwitchedToBuild =
        "[The user just switched to BUILD mode. You now have full tool access — proceed with any changes.]";

    // ── CreatePlan: after the plan is presented, tell the agent to wait for the user ────────
    internal const string PlanPresented =
        "The plan above has been presented to the user. You are STILL in Plan mode (read-only).\n" +
        "- Ask the user whether to implement it.\n" +
        "- If they approve → call ImplementPlan to switch to Build mode, then implement the plan.\n" +
        "- If they want changes → revise and call CreatePlan again with the updated plan.\n" +
        "Do NOT write any files until you have called ImplementPlan.";
}
