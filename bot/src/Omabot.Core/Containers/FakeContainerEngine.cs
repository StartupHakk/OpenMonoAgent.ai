// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace Omabot.Containers;

/// <summary>In-memory engine for tests and for `--dry-run`. Applies the same safety refusal as the real client.</summary>
public sealed class FakeContainerEngine : IContainerEngine
{
    private readonly ConcurrentDictionary<string, (ContainerSpec Spec, string Id, bool Running)> _byName = new(StringComparer.Ordinal);
    private int _next;

    public bool Available { get; set; } = true;
    public string? ImageId { get; set; } = "sha256:fake";
    public List<string> Calls { get; } = [];
    public Func<ContainerSpec, Exception?>? FailCreate { get; set; }

    public IReadOnlyDictionary<string, ContainerSpec> Created => _byName.ToDictionary(kv => kv.Key, kv => kv.Value.Spec);

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(Available);

    public Task<ContainerInfo?> InspectAsync(string name, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("inspect " + name);
        var hit = _byName.Values.FirstOrDefault(v => v.Spec.Name == name || v.Id == name);
        return Task.FromResult(hit.Spec is null ? null
            : new ContainerInfo(hit.Id, hit.Spec.Name, hit.Running ? "running" : "exited", hit.Running, hit.Spec.Labels, ImageId));
    }

    public Task<string> CreateAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("create " + spec.Name);
        var problems = spec.SafetyProblems();
        if (problems.Count > 0) throw new ContainerEngineException("Refused unsafe container spec: " + string.Join("; ", problems));
        if (FailCreate?.Invoke(spec) is { } ex) throw ex;
        if (_byName.ContainsKey(spec.Name)) throw new ContainerEngineException("Conflict: name in use", 409);
        var id = "fake" + Interlocked.Increment(ref _next).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        _byName[spec.Name] = (spec, id, false);
        return Task.FromResult(id);
    }

    public Task StartAsync(string idOrName, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("start " + idOrName);
        var key = Key(idOrName) ?? throw new ContainerEngineException("No such container", 404);
        _byName[key] = (_byName[key].Spec, _byName[key].Id, true);
        return Task.CompletedTask;
    }

    public Task StopAsync(string idOrName, int timeoutSeconds = 20, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("stop " + idOrName);
        var key = Key(idOrName);
        if (key is not null) _byName[key] = (_byName[key].Spec, _byName[key].Id, false);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string idOrName, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("remove " + idOrName);
        var key = Key(idOrName);
        if (key is not null) _byName.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContainerInfo>> ListAsync(string labelFilter, CancellationToken ct = default)
    {
        var parts = labelFilter.Split('=', 2);
        IReadOnlyList<ContainerInfo> list = [.. _byName.Values
            .Where(v => v.Spec.Labels.TryGetValue(parts[0], out var val) && (parts.Length == 1 || val == parts[1]))
            .Select(v => new ContainerInfo(v.Id, v.Spec.Name, v.Running ? "running" : "exited", v.Running, v.Spec.Labels, ImageId))];
        return Task.FromResult(list);
    }

    public Task<string?> ImageIdAsync(string image, CancellationToken ct = default) => Task.FromResult(ImageId);

    public Task EnsureNetworkAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Simulate a crash.</summary>
    public void Kill(string name)
    {
        if (_byName.TryGetValue(name, out var v)) _byName[name] = (v.Spec, v.Id, false);
    }

    private string? Key(string idOrName)
        => _byName.ContainsKey(idOrName) ? idOrName : _byName.FirstOrDefault(kv => kv.Value.Id == idOrName).Key;
}
