namespace OpenMono.Commands;

/// <summary>
/// Scaffolds a system-prompt or Plan-prompt override file (global or project-scoped) and
/// hands the path back to the user — the actual editing happens in their own editor.
/// SystemPrompt.BuildAsync / ModeInstructions.Activation pick the file up on next session start.
/// </summary>
public sealed class PromptCommand : ICommand
{
    public string Name => "prompt";
    public string Description => "Create/override the system prompt or Plan-mode prompt (global or project)";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var renderer = context.Renderer;
        var config = context.Config;

        // Program.cs hands each command everything after the command name as ONE unsplit
        // string in args[0] (e.g. "/prompt system project" -> args == ["system project"]).
        var tokens = args.Length > 0
            ? args[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        string which;
        if (tokens.Length > 0 && tokens[0] is "system" or "plan")
        {
            which = tokens[0];
        }
        else
        {
            renderer.WriteInfo(
                "Which prompt do you want to override?\n" +
                "  1. System prompt\n" +
                "  2. Plan-mode prompt (shown when EnterPlanMode is called)");
            var answer = await renderer.AskUserAsync("Choice [1/2]", ct);
            which = answer.Trim() is "2" or "plan" ? "plan" : "system";
        }

        bool global;
        if (tokens.Length > 1 && tokens[1] is "global" or "project")
        {
            global = tokens[1] == "global";
        }
        else
        {
            renderer.WriteInfo(
                "Scope?\n" +
                "  1. Global — ~/.openmono, applies to every project\n" +
                "  2. This project only — .openmono/ here");
            var answer = await renderer.AskUserAsync("Choice [1/2]", ct);
            global = answer.Trim() is "1" or "global";
        }

        var fileName = which == "plan" ? Config.PromptOverrides.PlanPromptFile : Config.PromptOverrides.SystemPromptFile;
        var path = global
            ? Config.PromptOverrides.GlobalPath(config, fileName)
            : Config.PromptOverrides.ProjectPath(config, fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var template = which == "plan"
                ? "<!-- Fully replaces the Plan-mode \"how to plan\" text shown when EnterPlanMode is called. -->\n\n"
                : "<!-- Fully replaces OpenMono's system prompt. Write the complete prompt below. -->\n\n";
            await File.WriteAllTextAsync(path, template, ct);
            renderer.WriteInfo($"Created {path}");
        }
        else
        {
            renderer.WriteInfo($"Override already exists at {path}");
        }

        renderer.WriteInfo("Edit that file, then start a new session to pick it up.");
    }
}
