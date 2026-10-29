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
        var registry = context.BackendRegistry;
        var backend = registry?.Get(ResolveBackendName(context.Session.ActiveBackend));
        var endpoint = backend?.Endpoint ?? context.Config.Llm.Endpoint;
        var apiKey = backend is not null && registry is not null && registry.HasKey(backend.Name)
            ? registry.GetKey(backend.Name)
            : context.Config.Llm.ApiKey;
        var current = string.IsNullOrWhiteSpace(context.Session.Model)
            ? context.Config.Llm.Model
            : context.Session.Model;
        var arg = args.Length > 0 ? string.Join(' ', args).Trim() : "";

        if (string.Equals(arg, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            context.Renderer.WriteInfo("Refreshing available models from backend…");
            var models = await ModelCatalog.RefreshAsync(
                endpoint, apiKey, ct: ct);
            context.Renderer.WriteInfo($"Found {models.Count} model(s).");
            arg = "";
        }

        // No target model: open the standardized selection menu over the available models.
        if (string.IsNullOrEmpty(arg))
        {
            var models = new List<string>(ModelCatalog.Models);
            if (models.Count == 0)
            {
                models = (await ModelCatalog.RefreshAsync(endpoint, apiKey, ct: ct)).ToList();
            }

            // Always include the current model so it's selectable and highlighted even when
            // the backend's /v1/models doesn't advertise it.
            if (!string.IsNullOrEmpty(current)
                && models.FindIndex(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                models.Insert(0, current);
            }

            if (models.Count == 0)
            {
                context.Renderer.WriteWarning(
                    "No models advertised by the backend. Use /model <name> to set one manually, or /model refresh to retry.");
                return;
            }

            var initial = models.FindIndex(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
            if (initial < 0) initial = 0;

            var picked = await context.Renderer.SelectOptionAsync(
                $"Select model — {endpoint}  (current: {(string.IsNullOrEmpty(current) ? "(none)" : current)})",
                models, initial, ct);

            if (picked is null)
            {
                context.Renderer.WriteInfo("Model selection cancelled.");
                return;
            }

            context.Session.Model = picked;
            Log.Info($"<---SWITCHED-MODEL-TO-{picked}--->");

            if (ModelCatalog.Contains(picked))
                context.Renderer.WriteInfo($"✓ Model switched to '{picked}' — takes effect on your next message.");
            else
                context.Renderer.WriteWarning(
                    $"✓ Model set to '{picked}' (not in the backend's advertised list — it may be rejected on send).");
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

        context.Session.Model = target;
        Log.Info($"<---SWITCHED-MODEL-TO-{target}--->");

        if (ModelCatalog.Contains(target))
            context.Renderer.WriteInfo($"✓ Model switched to '{target}' — takes effect on your next message.");
        else
            context.Renderer.WriteWarning(
                $"✓ Model set to '{target}' (not in the backend's advertised list — it may be rejected on send).");
    }

    private static string ResolveBackendName(string? active)
        => string.IsNullOrWhiteSpace(active) ? BackendRegistry.DefaultBackendName : active;
}
