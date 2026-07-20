using OpenMono.Llm;
using OpenMono.Utils;

namespace OpenMono.Commands;

/// <summary>
/// Shows the models the backend advertises as available and switches the active model at runtime.
/// The next conversation turn picks up <see cref="Config.LlmConfig.Model"/> automatically, so no
/// client rebuild is needed.
///   <c>/model</c>          — list available models, highlighting the current one
///   <c>/model &lt;name&gt;</c>  — switch to that model
///   <c>/model refresh</c>  — re-query the backend for available models
/// </summary>
public sealed class ModelCommand : ICommand
{
    public string Name => "model";
    public string Description => "List available models and switch the active model";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var current = context.Config.Llm.Model;
        var arg = args.Length > 0 ? string.Join(' ', args).Trim() : "";

        if (string.Equals(arg, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            context.Renderer.WriteInfo("Refreshing available models from backend…");
            var models = await ModelCatalog.RefreshAsync(
                context.Config.Llm.Endpoint, context.Config.Llm.ApiKey, ct: ct);
            context.Renderer.WriteInfo($"Found {models.Count} model(s).");
            arg = "";
        }

        // No target model: list what's available.
        if (string.IsNullOrEmpty(arg))
        {
            var models = ModelCatalog.Models;
            if (models.Count == 0)
            {
                models = await ModelCatalog.RefreshAsync(
                    context.Config.Llm.Endpoint, context.Config.Llm.ApiKey, ct: ct);
            }

            context.Renderer.WriteInfo($"Current model: {(string.IsNullOrEmpty(current) ? "(none)" : current)}");
            context.Renderer.WriteInfo($"Endpoint: {context.Config.Llm.Endpoint}");

            if (models.Count == 0)
            {
                context.Renderer.WriteWarning(
                    "No models advertised by the backend. Use /model <name> to set one manually, or /model refresh to retry.");
                return;
            }

            context.Renderer.WriteInfo("");
            context.Renderer.WriteInfo("Available models:");
            for (var i = 0; i < models.Count; i++)
            {
                var marker = string.Equals(models[i], current, StringComparison.OrdinalIgnoreCase) ? "→" : " ";
                context.Renderer.WriteInfo($"  {marker} {i + 1}. {models[i]}");
            }
            context.Renderer.WriteInfo("");
            context.Renderer.WriteInfo("Switch with: /model <name>  (type /model then a space to see suggestions)");
            return;
        }

        // Resolve the target: allow selection by 1-based index or by (partial) name.
        var available = ModelCatalog.Models;
        var target = arg;

        if (int.TryParse(arg, out var idx) && idx >= 1 && idx <= available.Count)
        {
            target = available[idx - 1];
        }
        else if (!ModelCatalog.Contains(arg))
        {
            var match = available.FirstOrDefault(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase));
            if (match is not null) target = match;
        }

        context.Config.Llm.Model = target;
        Log.Info($"<---SWITCHED-MODEL-TO-{target}--->");

        if (ModelCatalog.Contains(target))
            context.Renderer.WriteInfo($"✓ Model switched to '{target}' — takes effect on your next message.");
        else
            context.Renderer.WriteWarning(
                $"✓ Model set to '{target}' (not in the backend's advertised list — it may be rejected on send).");
    }
}
