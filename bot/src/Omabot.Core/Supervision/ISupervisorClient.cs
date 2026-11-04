// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Containers;

namespace Omabot.Supervision;

/// <summary>
/// What the roster may ask of the supervisor. The supervisor checks every
/// spec again before it talks to Docker; the roster never holds the socket.
/// </summary>
public interface ISupervisorClient
{
    Task<bool> PingAsync(CancellationToken ct = default);
    /// <summary>Create (or replace when the spec changed) and start. Returns the container id.</summary>
    Task<string> EnsureRunningAsync(ContainerSpec spec, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RemoveAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken ct = default);
    Task<string?> ImageIdAsync(string image, CancellationToken ct = default);
    /// <summary>Runs one routine command check in a throwaway container and removes it.</summary>
    Task<Routines.CommandCheckResult> RunCheckAsync(CheckContainerSpec spec, CancellationToken ct = default);
    /// <summary>Creates or verifies the roster networks (internal bots and desktop networks, the egress network).</summary>
    Task EnsureNetworksAsync(CancellationToken ct = default);
    /// <summary>Create (or replace when the spec changed) and start the egress gate or a desktop.</summary>
    Task<string> EnsureServiceAsync(ServiceContainerSpec spec, CancellationToken ct = default);
    Task StopServiceAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// In-process supervisor logic over an engine. The supervisor exe wraps this; tests use it with the fake engine.
/// In a web-multi user roster (<paramref name="tenant"/> set) every container name on the engine carries the
/// tenant prefix and a tenant label, the roster keeps its plain names, and the supervisor sees and changes only
/// its own tenant's containers.
/// </summary>
public sealed class DirectSupervisor(IContainerEngine engine, string network, IReadOnlyCollection<string>? checkRoots = null,
    Egress.NetworkPolicy? policy = null, IReadOnlyCollection<string>? serviceRoots = null, string? tenant = null) : ISupervisorClient
{
    public const string TenantLabel = "oma-bot.tenant";

    private string Physical(string name) => tenant is null ? name : $"{tenant}-{name}";

    private bool Mine(ContainerInfo c) => c.Labels.TryGetValue(TenantLabel, out var t) ? t == tenant : tenant is null;

    private ContainerInfo Logical(ContainerInfo c)
    {
        var name = c.Name.TrimStart('/');
        return tenant is not null && name.StartsWith(tenant + "-", StringComparison.Ordinal) ? c with { Name = name[(tenant.Length + 1)..] } : c with { Name = name };
    }

    private SortedDictionary<string, string> Labelled(IReadOnlyDictionary<string, string> labels)
    {
        var all = new SortedDictionary<string, string>(labels.ToDictionary(), StringComparer.Ordinal);
        if (tenant is not null) all[TenantLabel] = tenant;
        return all;
    }

    private async Task<ContainerInfo?> InspectMineAsync(string logical, CancellationToken ct)
    {
        var existing = await engine.InspectAsync(Physical(logical), ct).ConfigureAwait(false);
        if (existing is not null && !Mine(existing)) throw new ContainerEngineException($"Refused: the container named {Physical(logical)} belongs to another roster.");
        return existing;
    }
    /// <summary>
    /// Before any container with a network starts: the roster firewall rules
    /// must be in place (fail closed) and the roster networks must exist with
    /// the right subnet, bridge name and internal flag.
    /// </summary>
    private async Task NetworkGateAsync(CancellationToken ct)
    {
        if (policy is null)
        {
            await engine.EnsureNetworkAsync(network, ct).ConfigureAwait(false);
            return;
        }
        if (await policy.RefusalAsync(ct).ConfigureAwait(false) is { } why) throw new ContainerEngineException(why);
        await EnsureNetworksAsync(ct).ConfigureAwait(false);
    }

    public async Task EnsureNetworksAsync(CancellationToken ct = default)
    {
        if (policy is null)
        {
            await engine.EnsureNetworkAsync(network, ct).ConfigureAwait(false);
            return;
        }
        foreach (var n in Egress.Networks.All(policy.Config))
        {
            var info = await engine.InspectNetworkAsync(n.Name, ct).ConfigureAwait(false);
            if (info is null)
            {
                await engine.CreateNetworkAsync(n, ct).ConfigureAwait(false);
                continue;
            }
            var problem = info.Internal != n.Internal ? $"must {(n.Internal ? "" : "not ")}be internal"
                : info.Subnet != n.Subnet ? $"has subnet {info.Subnet ?? "none"}, not {n.Subnet}"
                : info.Bridge != n.Bridge ? $"has bridge {info.Bridge ?? "none"}, not {n.Bridge}"
                : null;
            if (problem is not null)
                throw new ContainerEngineException($"Refused: network {n.Name} {problem}. Remove it (docker network rm {n.Name}) and the roster creates it again.");
        }
    }

    public async Task<string> EnsureServiceAsync(ServiceContainerSpec spec, CancellationToken ct = default)
    {
        if (policy is null) throw new ContainerEngineException("Refused: the gate and desktops start only under the roster network policy.");
        var problems = spec.SafetyProblems(policy.Config, serviceRoots ?? []);
        if (serviceRoots is null || serviceRoots.Count == 0) problems = [.. problems, "the supervisor has no allowed roots for service containers"];
        if (problems.Count > 0) throw new ContainerEngineException("Refused unsafe service container: " + string.Join("; ", problems));
        await NetworkGateAsync(ct).ConfigureAwait(false);
        var existing = await InspectMineAsync(spec.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var same = existing.Labels.TryGetValue("oma-bot.spec", out var have) && spec.Labels.TryGetValue("oma-bot.spec", out var want) && have == want;
            if (same && existing.Running) return existing.Id;
            await engine.StopAsync(existing.Id, 20, ct).ConfigureAwait(false);
            await engine.RemoveAsync(existing.Id, ct).ConfigureAwait(false);
        }
        if (tenant is not null)
        {
            // The plain name stays resolvable on the tenant's own network, so bots still reach a desktop by name.
            var first = spec.Networks[0] with { Aliases = [.. spec.Networks[0].Aliases, spec.Name] };
            spec = spec with { Name = Physical(spec.Name), Labels = Labelled(spec.Labels), Networks = [first, .. spec.Networks.Skip(1)] };
        }
        var id = await engine.CreateRawAsync(spec.Name, spec.ToEngineJson(), ct).ConfigureAwait(false);
        foreach (var extra in spec.Networks.Skip(1)) await engine.ConnectNetworkAsync(extra.Network, id, extra.Aliases, ct).ConfigureAwait(false);
        await engine.StartAsync(id, ct).ConfigureAwait(false);
        return id;
    }

    public Task StopServiceAsync(string name, CancellationToken ct = default)
        => ServiceContainerSpec.IsServiceName(name) ? engine.StopAsync(Physical(name), 20, ct) : throw new ContainerEngineException("Refused: only the gate and desktops can be stopped this way.");

    public Task<bool> PingAsync(CancellationToken ct = default) => engine.PingAsync(ct);

    public async Task<Routines.CommandCheckResult> RunCheckAsync(CheckContainerSpec spec, CancellationToken ct = default)
    {
        var problems = spec.SafetyProblems(checkRoots ?? [], network);
        if (checkRoots is null || checkRoots.Count == 0) problems = [.. problems, "the supervisor has no allowed roots for check containers"];
        if (problems.Count > 0) throw new ContainerEngineException("Refused unsafe check container: " + string.Join("; ", problems));
        if (spec.Network != "none") await NetworkGateAsync(ct).ConfigureAwait(false);
        var body = spec.ToEngineJson();
        if (tenant is not null && body["Labels"] is System.Text.Json.Nodes.JsonObject labels) labels[TenantLabel] = tenant;
        var id = await engine.CreateRawAsync(Physical(spec.Name), body, ct).ConfigureAwait(false);
        try
        {
            await engine.StartAsync(id, ct).ConfigureAwait(false);
            var exit = await engine.WaitAsync(id, TimeSpan.FromSeconds(spec.TimeoutSeconds), ct).ConfigureAwait(false);
            if (exit is null)
            {
                await engine.KillAsync(id, CancellationToken.None).ConfigureAwait(false);
                return new Routines.CommandCheckResult(null, "", $"the check ran past its {spec.TimeoutSeconds} second limit and was stopped");
            }
            var logs = await engine.LogsAsync(id, 64 * 1024, ct).ConfigureAwait(false);
            return new Routines.CommandCheckResult(exit, Common.Redactor.Shared.Redact(logs), null);
        }
        finally
        {
            await engine.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<string> EnsureRunningAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        if (!spec.Name.StartsWith("oma-bot-", StringComparison.Ordinal) || !Common.Names.IsSafeSlug(spec.Name["oma-bot-".Length..]))
            throw new ContainerEngineException("Refused: container name must be oma-bot-<slug>.");
        var problems = spec.SafetyProblems().ToList();
        if (policy is not null)
        {
            if (spec.Network != Egress.Networks.Bots(policy.Config).Name) problems.Add($"a bot joins {Egress.Networks.Bots(policy.Config).Name}, not {spec.Network}");
            if ((spec.ExtraNetworks ?? []).Any(n => n != Egress.Networks.Desktops(policy.Config).Name)) problems.Add("a bot may also join only the desktop network");
        }
        if (problems.Count > 0) throw new ContainerEngineException("Refused unsafe container spec: " + string.Join("; ", problems));
        await NetworkGateAsync(ct).ConfigureAwait(false);
        var existing = await InspectMineAsync(spec.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var same = existing.Labels.TryGetValue("oma-bot.spec", out var hash) && spec.Labels.TryGetValue("oma-bot.spec", out var want) && hash == want;
            if (same && existing.Running) return existing.Id;
            // A stopped container that held a host invite is never restarted: its invite may have expired.
            var fresh = Fresh(spec.Labels) || Fresh(existing.Labels);
            if (same && !fresh)
            {
                await engine.StartAsync(existing.Id, ct).ConfigureAwait(false);
                return existing.Id;
            }
            await engine.StopAsync(existing.Id, 20, ct).ConfigureAwait(false);
            await engine.RemoveAsync(existing.Id, ct).ConfigureAwait(false);
        }
        var id = await engine.CreateAsync(tenant is null ? spec : spec with { Name = Physical(spec.Name), Labels = Labelled(spec.Labels) }, ct).ConfigureAwait(false);
        foreach (var extra in spec.ExtraNetworks ?? []) await engine.ConnectNetworkAsync(extra, id, [], ct).ConfigureAwait(false);
        await engine.StartAsync(id, ct).ConfigureAwait(false);
        return id;
    }

    public Task StopAsync(string name, CancellationToken ct = default) => engine.StopAsync(Physical(Guard(name)), 20, ct);

    public Task RemoveAsync(string name, CancellationToken ct = default) => engine.RemoveAsync(Physical(Guard(name)), ct);

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken ct = default)
        => [.. (await engine.ListAsync("oma-bot.roster=1", ct).ConfigureAwait(false)).Where(Mine).Select(Logical)];

    public Task<string?> ImageIdAsync(string image, CancellationToken ct = default) => engine.ImageIdAsync(image, ct);

    private static bool Fresh(IReadOnlyDictionary<string, string> labels)
        => labels.TryGetValue(ContainerSpec.FreshStartLabel, out var f) && f == "1";

    /// <summary>The supervisor only touches roster containers.</summary>
    private static string Guard(string name)
        => name.StartsWith("oma-bot-", StringComparison.Ordinal) && Common.Names.IsSafeSlug(name["oma-bot-".Length..])
            ? name
            : throw new ContainerEngineException("Refused: only oma-bot-<slug> containers can be changed.");
}
