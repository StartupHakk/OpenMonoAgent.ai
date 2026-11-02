using System.Collections.Generic;
using System.Linq;
using OpenMono.Llm;
using OpenMono.Rendering;

namespace OpenMono.Commands;

/// <summary>
/// Lists configured inference backends and switches the active backend for the current
/// session. The selected backend's endpoint + model become the target for all subsequent
/// turns. API keys are stored in-memory only (never written to disk).
///
///   /models                 - interactive menu (TUI: numbered keys; terminal: numbered prompt)
///   /models list            - non-interactive listing of all backends
///   /models add NAME endpoint [--model=NAME] [--nickname=NAME]
///   /models remove NAME
///   /models set NAME        - switch to NAME (prompts for key if none stored)
///   /models NAME            - same as set
/// </summary>
public sealed class ModelsCommand : ICommand
{
    public string Name => "models";
    public string Description => "List backends, add/remove, or switch the active inference backend";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var registry = context.BackendRegistry;
        var pool = context.LlmClientPool;

        if (registry is null)
        {
            context.Renderer.WriteError("Backend registry not available in this context.");
            return;
        }

        var arg = args.Length > 0 ? string.Join(' ', args).Trim() : "";

        if (string.IsNullOrEmpty(arg))
        {
            await ShowMenuAsync(context, registry, ct);
            return;
        }

        var parts = arg.Split(' ', 2);
        var sub = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        switch (sub)
        {
            case "list":
                await ShowListAsync(context, registry, ct);
                return;

            case "add":
                await AddBackendAsync(context, registry, rest, ct);
                return;

            case "remove":
                if (string.IsNullOrEmpty(rest))
                {
                    context.Renderer.WriteWarning("Usage: /models remove NAME");
                    return;
                }
                if (!registry.Has(rest))
                {
                    context.Renderer.WriteWarning($"Unknown backend '{rest}'.");
                    return;
                }
                registry.Remove(rest);
                pool?.Invalidate(rest);
                context.Renderer.WriteInfo($"Backend '{rest}' removed.");
                return;

            case "set":
            case "":
                // "/models NAME" or "/models set NAME" — NAME may be a name or nickname
                if (string.IsNullOrEmpty(rest))
                {
                    context.Renderer.WriteWarning("Usage: /models set NAME  (or just /models for the menu)");
                    return;
                }
                var resolved = registry.Resolve(rest);
                if (resolved is null)
                {
                    context.Renderer.WriteWarning($"Unknown backend '{rest}'. Use /models list to see available backends.");
                    return;
                }
                await SwitchBackendAsync(context, registry, resolved.Name, ct);
                return;

            default:
                context.Renderer.WriteWarning(
                    "Usage: /models | /models list | /models add NAME endpoint | " +
                    "/models remove NAME | /models set NAME");
                return;
        }
    }

    private async Task ShowListAsync(CommandContext context, BackendRegistry registry, CancellationToken ct)
    {
        var backends = registry.All;
        if (backends.Count == 0)
        {
            context.Renderer.WriteWarning("No backends configured. Use /models add NAME endpoint to add one.");
            return;
        }

        var probes = await ProbeBackendsAsync(registry, ct);

        context.Renderer.WriteInfo("Configured backends:");
        foreach (var b in backends)
        {
            var marker = string.Equals(b.Name, context.Session.ActiveBackend, StringComparison.OrdinalIgnoreCase) ? "->" : "  ";
            context.Renderer.WriteInfo($"  {marker} {Describe(b, registry, probes.GetValueOrDefault(b.Name))}");
        }
        context.Renderer.WriteInfo("");
        context.Renderer.WriteInfo("Switch with: /models set NAME  (type /models then a space to see suggestions)");
    }

    /// <summary>Probes every backend's <c>/v1/models</c> endpoint; maps name → probe result.</summary>
    private static Task<Dictionary<string, ModelCatalog.BackendProbe>> ProbeBackendsAsync(
        BackendRegistry registry, CancellationToken ct)
        => ModelCatalog.ProbeAsync(
            registry.All.Select(b => (b.Name, b.Endpoint, registry.GetKey(b.Name))), ct);

    /// <summary>
    /// Renders a backend row as "[nickname-or-name] &lt;model&gt;  endpoint [key]".
    /// The model slot shows the backend's default model, <c>MULTI (N)</c> for a reachable
    /// server advertising several models with no single default, or <c>OFFLINE</c> when the
    /// endpoint cannot be reached.
    /// </summary>
    private static string Describe(BackendDefinition b, BackendRegistry reg, ModelCatalog.BackendProbe? probe)
    {
        var label = string.IsNullOrWhiteSpace(b.Nickname) ? b.Name : b.Nickname;
        var model = probe is { Ok: false } ? "OFFLINE"
            : !string.IsNullOrWhiteSpace(b.DefaultModel) ? b.DefaultModel
            : probe is { Ok: true, IsMulti: true } ? $"MULTI ({probe.Models.Count})"
            : "(no default model)";
        var keyStatus = reg.HasKey(b.Name) ? " [key]" : "";
        return $"[{label}] {model}  {b.Endpoint}{keyStatus}";
    }

    private async Task ShowMenuAsync(CommandContext context, BackendRegistry registry, CancellationToken ct)
    {
        var backends = registry.All.ToList();
        if (backends.Count == 0)
        {
            context.Renderer.WriteWarning("No backends configured. Use /models add NAME endpoint to add one.");
            return;
        }

        var probes = await ProbeBackendsAsync(registry, ct);

        var choices = backends.Select(b => Describe(b, registry, probes.GetValueOrDefault(b.Name))).ToList();

        // Standardized selection menu (TUI: input-lane overlay; terminal: numbered prompt).
        var activeIdx = backends.FindIndex(b =>
            string.Equals(b.Name, context.Session.ActiveBackend, StringComparison.OrdinalIgnoreCase));
        if (activeIdx < 0) activeIdx = 0;

        var picked = await context.Renderer.SelectOptionAsync("Select backend", choices, activeIdx, ct);
        if (picked is null)
        {
            context.Renderer.WriteInfo("Backend selection cancelled.");
            return;
        }

        for (var i = 0; i < backends.Count; i++)
        {
            if (string.Equals(choices[i], picked, StringComparison.Ordinal))
            {
                await SwitchBackendAsync(context, registry, backends[i].Name, ct);
                return;
            }
        }
    }

    private async Task AddBackendAsync(CommandContext context, BackendRegistry registry, string rest, CancellationToken ct)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            context.Renderer.WriteWarning("Usage: /models add NAME endpoint [--model=NAME]");
            return;
        }

        var name = parts[0];
        var endpoint = parts.Length > 1 ? parts[1] : "";

        if (string.IsNullOrEmpty(endpoint))
        {
            context.Renderer.WriteWarning("Usage: /models add NAME endpoint [--model=NAME]");
            return;
        }

        if (registry.Has(name))
        {
            context.Renderer.WriteWarning($"Backend '{name}' already exists. Use /models set NAME to switch.");
            return;
        }

        string? defaultModel = null, nickname = null;
        foreach (var p in parts.Skip(2))
        {
            if (p.StartsWith("--model=", StringComparison.OrdinalIgnoreCase))
                defaultModel = p["--model=".Length..].Trim();
            else if (p.StartsWith("--nickname=", StringComparison.OrdinalIgnoreCase))
                nickname = p["--nickname=".Length..].Trim();
        }

        var def = new BackendDefinition
        {
            Name = name,
            Endpoint = endpoint,
            DefaultModel = defaultModel,
            Nickname = string.IsNullOrEmpty(nickname) ? null : nickname,
            IsLocal = endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
        };

        registry.Add(def);

        if (!def.IsLocal)
        {
            var key = await PromptForKeyAsync(context, name, ct);
            if (!string.IsNullOrEmpty(key))
            {
                registry.SetKey(name, key);
                context.Renderer.WriteInfo($"Key stored in memory for '{name}' (lost on restart).");
            }
        }

        context.Renderer.WriteInfo($"Backend '{name}' added: {endpoint}");
        context.Renderer.WriteInfo($"Default model: {def.DefaultModel ?? "(none)"}");
    }

    private async Task SwitchBackendAsync(CommandContext context, BackendRegistry registry, string name, CancellationToken ct)
    {
        if (!registry.Has(name))
        {
            context.Renderer.WriteWarning($"Unknown backend '{name}'. Use /models list to see available backends.");
            return;
        }

        var def = registry.Get(name)!;

        if (!registry.HasKey(name) && !def.IsLocal)
        {
            var key = await PromptForKeyAsync(context, name, ct);
            if (string.IsNullOrEmpty(key))
            {
                context.Renderer.WriteWarning("No API key provided for '{name}' and it is not a local backend.");
                return;
            }
            registry.SetKey(name, key);
        }

        context.LlmClientPool?.Invalidate(name);

        context.Session.ActiveBackend = name;
        context.Session.Model = def.DefaultModel;

        context.Renderer.WriteInfo($"Switched to backend '{name}': {def.Endpoint}");
        context.Renderer.WriteInfo($"  Model: {context.Session.Model ?? "(default)"}");
        context.Renderer.WriteInfo("  Takes effect on your next message.");
    }

    private async Task<string> PromptForKeyAsync(CommandContext context, string name, CancellationToken ct)
    {
        if (context.Renderer is AnsiTuiRenderer tui)
        {
            var secret = await tui.AskSecretAsync($"API key for backend '{name}':", ct);
            return new string(secret);
        }

        context.Renderer.WriteWarning("Terminal mode: API key will be visible in the terminal. " +
            "Consider using /models in TUI mode or the ACP API for secure key entry.");
        var key = await context.Renderer.AskUserAsync($"API key for backend '{name}':", ct);
        return key;
    }
}
