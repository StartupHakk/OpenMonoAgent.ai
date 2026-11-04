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
}

/// <summary>In-process supervisor logic over an engine. The supervisor exe wraps this; tests use it with the fake engine.</summary>
public sealed class DirectSupervisor(IContainerEngine engine, string network) : ISupervisorClient
{
    public Task<bool> PingAsync(CancellationToken ct = default) => engine.PingAsync(ct);

    public async Task<string> EnsureRunningAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        if (!spec.Name.StartsWith("oma-bot-", StringComparison.Ordinal) || !Common.Names.IsSafeSlug(spec.Name["oma-bot-".Length..]))
            throw new ContainerEngineException("Refused: container name must be oma-bot-<slug>.");
        var problems = spec.SafetyProblems();
        if (problems.Count > 0) throw new ContainerEngineException("Refused unsafe container spec: " + string.Join("; ", problems));
        await engine.EnsureNetworkAsync(network, ct).ConfigureAwait(false);
        var existing = await engine.InspectAsync(spec.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var same = existing.Labels.TryGetValue("oma-bot.spec", out var hash) && spec.Labels.TryGetValue("oma-bot.spec", out var want) && hash == want;
            if (same && existing.Running) return existing.Id;
            if (same)
            {
                await engine.StartAsync(existing.Id, ct).ConfigureAwait(false);
                return existing.Id;
            }
            await engine.StopAsync(existing.Id, 20, ct).ConfigureAwait(false);
            await engine.RemoveAsync(existing.Id, ct).ConfigureAwait(false);
        }
        var id = await engine.CreateAsync(spec, ct).ConfigureAwait(false);
        await engine.StartAsync(id, ct).ConfigureAwait(false);
        return id;
    }

    public Task StopAsync(string name, CancellationToken ct = default) => engine.StopAsync(Guard(name), 20, ct);

    public Task RemoveAsync(string name, CancellationToken ct = default) => engine.RemoveAsync(Guard(name), ct);

    public Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken ct = default) => engine.ListAsync("oma-bot.roster=1", ct);

    public Task<string?> ImageIdAsync(string image, CancellationToken ct = default) => engine.ImageIdAsync(image, ct);

    /// <summary>The supervisor only touches roster containers.</summary>
    private static string Guard(string name)
        => name.StartsWith("oma-bot-", StringComparison.Ordinal) && Common.Names.IsSafeSlug(name["oma-bot-".Length..])
            ? name
            : throw new ContainerEngineException("Refused: only oma-bot-<slug> containers can be changed.");
}
