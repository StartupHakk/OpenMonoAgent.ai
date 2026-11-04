// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Containers;

public sealed record ContainerInfo(string Id, string Name, string State, bool Running, IReadOnlyDictionary<string, string> Labels, string? ImageId);

/// <summary>What Docker reports about a network: whether it is internal, its subnet and its Linux bridge name.</summary>
public sealed record NetworkInfo(string Name, bool Internal, string? Subnet, string? Bridge);

public sealed class ContainerEngineException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The small slice of the Docker Engine API the supervisor uses.</summary>
public interface IContainerEngine
{
    Task<bool> PingAsync(CancellationToken ct = default);
    Task<ContainerInfo?> InspectAsync(string name, CancellationToken ct = default);
    Task<string> CreateAsync(ContainerSpec spec, CancellationToken ct = default);
    Task StartAsync(string idOrName, CancellationToken ct = default);
    Task StopAsync(string idOrName, int timeoutSeconds = 20, CancellationToken ct = default);
    Task RemoveAsync(string idOrName, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerInfo>> ListAsync(string labelFilter, CancellationToken ct = default);
    Task<string?> ImageIdAsync(string image, CancellationToken ct = default);
    Task EnsureNetworkAsync(string name, CancellationToken ct = default);
    Task<NetworkInfo?> InspectNetworkAsync(string name, CancellationToken ct = default);
    Task CreateNetworkAsync(Egress.NetworkSpec spec, CancellationToken ct = default);
    Task ConnectNetworkAsync(string network, string container, IReadOnlyList<string> aliases, CancellationToken ct = default);
    /// <summary>Creates a container from an already checked engine body (check and probe containers).</summary>
    Task<string> CreateRawAsync(string name, System.Text.Json.Nodes.JsonObject body, CancellationToken ct = default);
    /// <summary>Waits for a container to exit. Null when the time limit passes first.</summary>
    Task<long?> WaitAsync(string idOrName, TimeSpan limit, CancellationToken ct = default);
    /// <summary>Standard output and error, at most <paramref name="maxBytes"/>.</summary>
    Task<string> LogsAsync(string idOrName, int maxBytes, CancellationToken ct = default);
    Task KillAsync(string idOrName, CancellationToken ct = default);
}
