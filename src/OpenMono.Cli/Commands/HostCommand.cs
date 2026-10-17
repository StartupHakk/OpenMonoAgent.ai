using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Commands;

/// <summary>
/// Host scope: sandboxed (workspace-only tools) vs whole-machine (HOST_EXEC
/// relayed to a host-bridge daemon). When the launcher started a daemon for
/// this run, the session is already paired and <c>on</c> is a no-op; otherwise
/// <c>on</c> pairs against a running daemon. The HOST:ON/OFF status-bar chip
/// (AnsiPainter) is the always-visible counterpart.
/// </summary>
public sealed class HostCommand : ICommand
{
    public string Name => "host";
    public string Description => "Show or switch host scope (sandboxed vs whole-machine)";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var sub = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "status";
        var gateway = HostGatewayRegistry.Get(context.Session.Id);

        switch (sub)
        {
            case "status":
            case "":
                if (gateway is { IsPaired: true } && !await gateway.IsPairingLiveAsync(ct))
                    context.Renderer.WriteInfo(
                        "HOST:STALE — the agent still holds a pairing token, but the host daemon no longer " +
                        "recognizes it (the daemon restarted or revoked it). Run: /host on to re-pair.");
                else if (gateway is { IsPaired: true })
                    context.Renderer.WriteInfo(
                        $"HOST:ON — paired to {gateway.BaseUrl} (run as {gateway.RunAs}, sudo={gateway.Sudo}). " +
                        "Host actions go through a HOST EXECUTION consent card, run on bare metal, audited + policy-gated." +
                        (gateway.SessionAllowsAll ? " Allow-all is active for this session." : "") +
                        (gateway.SessionDeniesAll ? " Deny-all is active for this session." : ""));
                else
                    context.Renderer.WriteInfo(
                        "HOST:OFF — sandboxed: workspace-only tools, no host access. " +
                        "Turn it on with: /host on (needs the host daemon running: openmono host daemon)");
                break;

            case "on":
                if (gateway is { IsPaired: true } && await gateway.IsPairingLiveAsync(ct))
                {
                    context.Renderer.WriteInfo($"HOST:ON already — paired to {gateway.BaseUrl}.");
                    break;
                }
                if (gateway is { IsPaired: true })
                {
                    // Stale token (daemon restarted or revoked us): the daemon
                    // would reject every exec with it, so drop it locally and
                    // re-pair instead of no-opping on the dead state.
                    context.Renderer.WriteInfo("Existing pairing is stale (daemon restarted?) — re-pairing ...");
                    HostGatewayRegistry.Unregister(context.Session.Id);
                }
                context.Renderer.WriteInfo($"Pairing with host daemon at {HostGatewayBootstrap.ResolveBaseUrl()} ...");
                if (!HostGatewayBootstrap.HasCredential())
                {
                    // Request mode: pairing now waits for a host-side
                    // approval, so hand the operator the exact command
                    // before the wait starts instead of failing silently.
                    context.Renderer.WriteInfo(
                        $"This sandbox has no pairing credential, so the daemon will treat this as a " +
                        $"pair *request*. On the host machine run: openmono host approve {context.Session.Id} " +
                        "(or: openmono host requests to see pending ones).");
                }
                var (paired, error) = await HostGatewayBootstrap.TryPairAsync(context.Session, ct);
                if (paired is null)
                {
                    context.Renderer.WriteError("Could not pair: " + (error ?? "unknown error"));
                    break;
                }
                context.Session.Meta.HostEnabled = true;
                InjectStandingOrders(context, paired);
                context.Renderer.WriteInfo(
                    $"HOST:ON — paired to {paired.BaseUrl} (run as {paired.RunAs}, sudo={paired.Sudo}). " +
                    "Every host command now shows a HOST EXECUTION card before running on bare metal.");
                break;

            case "off":
                if (gateway is null || !gateway.IsPaired)
                {
                    context.Renderer.WriteInfo("HOST:OFF already — sandboxed to the workspace.");
                    break;
                }
                await gateway.UnpairAsync(ct);
                HostGatewayRegistry.Unregister(context.Session.Id);
                context.Session.Meta.HostEnabled = false;
                context.Renderer.WriteInfo("HOST:OFF — pairing revoked, back to workspace-only tools.");
                break;

            default:
                context.Renderer.WriteInfo("Usage: /host [status|on|off]");
                break;
        }
    }

    private static void InjectStandingOrders(CommandContext context, HostGateway gateway)
    {
        var orders = gateway.BuildStandingOrders();
        if (orders is null) return;
        // Append to the live system message so the standing orders take effect
        // for the rest of the session without rebuilding the whole prompt.
        var system = context.Session.Messages.FirstOrDefault(m => m.Role == MessageRole.System);
        if (system is not null)
            context.Session.Messages[context.Session.Messages.IndexOf(system)] =
                system with { Content = system.Content + "\n\n" + orders };
        else
            context.Session.Messages.Insert(0, new Message { Role = MessageRole.System, Content = orders });
    }
}
