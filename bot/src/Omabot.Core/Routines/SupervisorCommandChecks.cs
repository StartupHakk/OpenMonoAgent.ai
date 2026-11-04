// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Config;
using Omabot.Containers;
using Omabot.Supervision;

namespace Omabot.Routines;

/// <summary>
/// Command checks through the supervisor (plan 7.4): never on the host, always
/// in a throwaway container with no network. A check that asks for network
/// gets the roster network and the egress gate proxy, and is refused when the
/// gate is not set up, so it can never reach the LAN directly.
/// </summary>
public sealed class SupervisorCommandChecks(ISupervisorClient supervisor, RosterConfig config, string appDir, Func<string, string?>? egressProxyFor = null) : ICommandCheckRunner
{
    public async Task<CommandCheckResult> RunAsync(string botSlug, string script, IReadOnlyList<string> args, string workspace, bool network, TimeSpan timeout, CancellationToken ct)
    {
        var netName = "none";
        Dictionary<string, string>? env = null;
        if (network)
        {
            var proxy = config.Egress.Enabled ? egressProxyFor?.Invoke(botSlug) : null;
            if (proxy is null) return new CommandCheckResult(null, "", "this check asks for network, which needs the egress gate; it is not set up");
            netName = config.Bots.Network;
            env = new Dictionary<string, string> { ["HTTPS_PROXY"] = proxy, ["HTTP_PROXY"] = proxy, ["NO_PROXY"] = "" };
        }
        var writable = script == "git-fetch.sh";
        var spec = CheckContainerSpec.For(botSlug, script, args, Path.Combine(appDir, "checks"), Path.GetFullPath(workspace), writable, netName, env, config, timeout);
        try
        {
            return await supervisor.RunCheckAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContainerEngineException or HttpRequestException or InvalidOperationException)
        {
            return new CommandCheckResult(null, "", Common.Redactor.Shared.Redact(ex.Message));
        }
    }
}
