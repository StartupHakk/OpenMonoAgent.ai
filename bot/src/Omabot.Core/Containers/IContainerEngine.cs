// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Containers;

public sealed record ContainerInfo(string Id, string Name, string State, bool Running, IReadOnlyDictionary<string, string> Labels, string? ImageId);

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
}
