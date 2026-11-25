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

            if (models.Count == 1)
            {
                context.Renderer.WriteInfo(
                    $"Endpoint advertises one model ({models[0]}) — nothing to pick. " +
                    $"Use /model <name> to set a model the endpoint doesn't advertise, or /model refresh to retry.");
                return;
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
            await ApplyModelSwitchAsync(context, picked, ct);
            WriteModelDivider(context, picked);

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
        await ApplyModelSwitchAsync(context, target, ct);
        WriteModelDivider(context, target);

        if (ModelCatalog.Contains(target))
            context.Renderer.WriteInfo($"✓ Model switched to '{target}' — takes effect on your next message.");
        else
            context.Renderer.WriteWarning(
                $"✓ Model set to '{target}' (not in the backend's advertised list — it may be rejected on send).");
    }

    /// <summary>
    /// After the active model changed: re-probe (or reuse the cached) reasoning
    /// capabilities for the new (endpoint, model) and clamp the session's
    /// thinking level to what the new server actually advertises.
    /// </summary>
    private static async Task ApplyModelSwitchAsync(CommandContext context, string model, CancellationToken ct)
    {
        var endpoint = context.BackendRegistry?.Get(ResolveBackendName(context.Session.ActiveBackend))?.Endpoint
            ?? context.Config.Llm.Endpoint;
        var apiKey = context.Config.Llm.ApiKey;
        context.Config.Capabilities.Invalidate(endpoint, model);
        var info = await Utils.ReasoningCapabilityCache
            .ProbeAsync(endpoint, apiKey, ct).ConfigureAwait(false);
        if (info is not null)
            context.Config.Capabilities.Set(endpoint, model, info);

        // Adopt the new model's advertised n_ctx so the ctx gauge denominator
        // follows the active model. An explicit llm.context_size always wins.
        if (info?.ContextWindow is > 0 && !context.Config.Llm.ContextSizeExplicit)
            context.Config.Llm.ContextSize =
                Utils.ContextSizeResolver.ResolveContextSize(info.ContextWindow, context.Config.Llm.ContextSize);

        var settings = context.Session.Settings
            ??= OpenMono.Session.SessionSettings.Attach(
                context.Session, "tui",
                reasoningProfile: () => OpenMono.Utils.ModelReasoningProfile.Resolve(
                    context.Session.Model,
                    context.Config.Capabilities.Get(endpoint, context.Session.Model) ?? context.Config.Llm.ServerReasoning));

        if (await settings.ClampThinkingToCapabilitiesAsync(ct))
            context.Renderer.WriteInfo(
                $"Thinking level adjusted to '{context.Session.Meta.ThinkingLevel}' for '{model}'.");
    }

    /// <summary>
    /// Draws a transcript divider marking the model/endpoint switch, mirroring
    /// the extension's model divider so the TUI shows where the switch happened.
    /// </summary>
    private static void WriteModelDivider(CommandContext context, string model)
    {
        var backend = ResolveBackendName(context.Session.ActiveBackend);
        var label = string.Equals(backend, BackendRegistry.DefaultBackendName, StringComparison.OrdinalIgnoreCase)
            ? $" Switched model · {model} "
            : $" Switched model · {backend} · {model} ";
        const int width = 56;
        var fill = Math.Max(2, (width - label.Length) / 2);
        context.Renderer.WriteInfo($"{new string('\u2500', fill)}{label}{new string('\u2500', fill)}");
    }

    private static string ResolveBackendName(string? active)
        => string.IsNullOrWhiteSpace(active) ? BackendRegistry.DefaultBackendName : active;
}
