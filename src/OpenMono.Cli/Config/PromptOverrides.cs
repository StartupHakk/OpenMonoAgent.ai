namespace OpenMono.Config;

/// <summary>
/// User-authored overrides for the system prompt (SystemPrompt.Base) and the Plan-mode
/// Activation text (ModeInstructions.Activation). Project file wins over the global one —
/// same &lt;workdir&gt;/.openmono vs ~/.openmono layering ConfigLoader uses for settings.json.
/// </summary>
public static class PromptOverrides
{
    public const string SystemPromptFile = "system-prompt.md";
    public const string PlanPromptFile = "plan-prompt.md";

    public static string? LoadSystemPrompt(AppConfig config) => Load(config, SystemPromptFile);
    public static string? LoadPlanPrompt(AppConfig config) => Load(config, PlanPromptFile);

    public static string ProjectPath(AppConfig config, string fileName) =>
        Path.Combine(config.WorkingDirectory, ".openmono", fileName);

    public static string GlobalPath(AppConfig config, string fileName) =>
        Path.Combine(config.DataDirectory, fileName);

    private static string? Load(AppConfig config, string fileName)
    {
        var projectPath = ProjectPath(config, fileName);
        if (File.Exists(projectPath)) return File.ReadAllText(projectPath);

        var globalPath = GlobalPath(config, fileName);
        if (File.Exists(globalPath)) return File.ReadAllText(globalPath);

        return null;
    }
}
