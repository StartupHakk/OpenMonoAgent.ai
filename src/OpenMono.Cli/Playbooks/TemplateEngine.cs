using System.Text.RegularExpressions;
using OpenMono.Utils;

namespace OpenMono.Playbooks;

public static partial class TemplateEngine
{

    public static async Task<string> ResolveAsync(
        string template,
        PlaybookState state,
        PlaybookDefinition playbook,
        string workingDirectory,
        CancellationToken ct)
    {
        var result = template;

        result = ParamPattern().Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            return state.Parameters.TryGetValue(key, out var val) ? val?.ToString() ?? "" : match.Value;
        });

        result = StatePattern().Replace(result, match =>
        {
            var key = match.Groups[1].Value;
            return state.StepOutputs.TryGetValue(key, out var val) ? val : match.Value;
        });

        result = result.Replace("{{constraints}}", RenderConstraints(playbook.Constraints));

        result = result.Replace("{{playbook.base-path}}", playbook.BasePath);

        result = result.Replace("{{env.CWD}}", workingDirectory);
        result = result.Replace("{{env.DATE}}", DateTime.UtcNow.ToString("yyyy-MM-dd"));

        var branch = await GitHelper.GetCurrentBranchAsync(workingDirectory, ct);
        result = result.Replace("{{env.GIT_BRANCH}}", branch ?? "unknown");

        result = await ResolveFileReferencesAsync(result, workingDirectory, ct);

        result = await ResolveShellCommandsAsync(result, workingDirectory, ct);

        return result;
    }

    private static string RenderConstraints(ConstraintSet constraints)
    {
        var lines = new List<string>();

        if (constraints.File is not null && File.Exists(constraints.File))
            lines.Add(File.ReadAllText(constraints.File));

        foreach (var c in constraints.Inline)
            lines.Add($"- {c}");

        return lines.Count > 0 ? string.Join('\n', lines) : "(no constraints)";
    }

    private static async Task<string> ResolveFileReferencesAsync(
        string template, string cwd, CancellationToken ct)
    {
        var matches = FilePattern().Matches(template);
        var result = template;

        foreach (Match match in matches)
        {
            var path = Path.GetFullPath(match.Groups[1].Value, cwd);
            var content = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "(file not found)";
            result = result.Replace(match.Value, content);
        }

        return result;
    }

    private static async Task<string> ResolveShellCommandsAsync(
        string template, string cwd, CancellationToken ct)
    {
        var matches = ShellPattern().Matches(template);
        var result = template;

        foreach (Match match in matches)
        {
            var command = match.Groups[1].Value;
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(command, cwd, timeoutMs: 10_000, ct: ct);
            var output = exit == 0 ? stdout : $"(exit {exit}) {stderr}";
            result = result.Replace(match.Value, output);
        }

        return result;
    }

    [GeneratedRegex(@"\{\{params\.(\w+)\}\}")]
    private static partial Regex ParamPattern();

    [GeneratedRegex(@"\{\{state\.(\w+)\}\}")]
    private static partial Regex StatePattern();

    [GeneratedRegex(@"\{\{file:([^}]+)\}\}")]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"\{\{shell:([^}]+)\}\}")]
    private static partial Regex ShellPattern();
}
