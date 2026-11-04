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
        if (hit.Spec is null)
        {
            var raw = _raw.FirstOrDefault(kv => kv.Value.Name == name || kv.Key == name);
            return Task.FromResult(raw.Value.Body is null ? null
                : new ContainerInfo(raw.Key, raw.Value.Name, _rawRunning.ContainsKey(raw.Key) ? "running" : "exited", _rawRunning.ContainsKey(raw.Key), RawLabels(raw.Value.Body), ImageId));
        }
        return Task.FromResult<ContainerInfo?>(new ContainerInfo(hit.Id, hit.Spec.Name, hit.Running ? "running" : "exited", hit.Running, hit.Spec.Labels, ImageId));
    }

    private static IReadOnlyDictionary<string, string> RawLabels(System.Text.Json.Nodes.JsonObject body)
        => body["Labels"] is System.Text.Json.Nodes.JsonObject l ? l.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "") : new Dictionary<string, string>();

    private readonly ConcurrentDictionary<string, bool> _rawRunning = new(StringComparer.Ordinal);

    /// <summary>Networks created or found, by name.</summary>
    public ConcurrentDictionary<string, NetworkInfo> Networks { get; } = new(StringComparer.Ordinal);

    /// <summary>Extra network attachments: (network, container, aliases).</summary>
    public List<(string Network, string Container, IReadOnlyList<string> Aliases)> Connected { get; } = [];

    public Task<NetworkInfo?> InspectNetworkAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Networks.TryGetValue(name, out var n) ? n : null);

    public Task CreateNetworkAsync(Egress.NetworkSpec spec, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("network-create " + spec.Name);
        Networks[spec.Name] = new NetworkInfo(spec.Name, spec.Internal, spec.Subnet, spec.Bridge);
        return Task.CompletedTask;
    }

    public Task ConnectNetworkAsync(string network, string container, IReadOnlyList<string> aliases, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"connect {network} {container}");
        lock (Connected) Connected.Add((network, container, aliases));
        return Task.CompletedTask;
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
        if (RawKey(idOrName) is { } rk)
        {
            _rawRunning[rk] = true;
            return Task.CompletedTask;
        }
        var key = Key(idOrName) ?? throw new ContainerEngineException("No such container", 404);
        _byName[key] = (_byName[key].Spec, _byName[key].Id, true);
        return Task.CompletedTask;
    }

    public Task StopAsync(string idOrName, int timeoutSeconds = 20, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("stop " + idOrName);
        var key = Key(idOrName);
        if (key is not null) _byName[key] = (_byName[key].Spec, _byName[key].Id, false);
        if (RawKey(idOrName) is { } rk) _rawRunning.TryRemove(rk, out _);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string idOrName, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("remove " + idOrName);
        var key = Key(idOrName);
        if (key is not null) _byName.TryRemove(key, out _);
        if (RawKey(idOrName) is { } rk)
        {
            RemovedRaw++;
            _raw.TryRemove(rk, out _);
            _rawRunning.TryRemove(rk, out _);
        }
        return Task.CompletedTask;
    }

    /// <summary>How many raw containers were removed (a check container must always be cleaned up).</summary>
    public int RemovedRaw { get; private set; }

    public Task<IReadOnlyList<ContainerInfo>> ListAsync(string labelFilter, CancellationToken ct = default)
    {
        var parts = labelFilter.Split('=', 2);
        IReadOnlyList<ContainerInfo> list = [.. _byName.Values
            .Where(v => v.Spec.Labels.TryGetValue(parts[0], out var val) && (parts.Length == 1 || val == parts[1]))
            .Select(v => new ContainerInfo(v.Id, v.Spec.Name, v.Running ? "running" : "exited", v.Running, v.Spec.Labels, ImageId))
            .Concat(_raw.Where(kv => RawLabels(kv.Value.Body).TryGetValue(parts[0], out var rv) && (parts.Length == 1 || rv == parts[1]))
                .Select(kv => new ContainerInfo(kv.Key, kv.Value.Name, _rawRunning.ContainsKey(kv.Key) ? "running" : "exited", _rawRunning.ContainsKey(kv.Key), RawLabels(kv.Value.Body), ImageId)))];
        return Task.FromResult(list);
    }

    public Task<string?> ImageIdAsync(string image, CancellationToken ct = default) => Task.FromResult(ImageId);

    public Task EnsureNetworkAsync(string name, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("network " + name);
        return Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, (string Name, System.Text.Json.Nodes.JsonObject Body)> _raw = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, System.Text.Json.Nodes.JsonObject> _rawHistory = new(StringComparer.Ordinal);

    /// <summary>Every raw container ever created (check, gate and desktop containers), by name, last body wins.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> RawCreated => _rawHistory.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>What a raw container does when it runs: exit code (null means it never exits) and its output.</summary>
    public Func<System.Text.Json.Nodes.JsonObject, (long? Exit, string Logs)> OnRaw { get; set; } = _ => (1, "");

    public Task<string> CreateRawAsync(string name, System.Text.Json.Nodes.JsonObject body, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("create-raw " + name);
        var id = "raw" + Interlocked.Increment(ref _next).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        _raw[id] = (name, body);
        _rawHistory[name] = body;
        return Task.FromResult(id);
    }

    public Task<long?> WaitAsync(string idOrName, TimeSpan limit, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("wait " + idOrName);
        return Task.FromResult(_raw.TryGetValue(idOrName, out var r) ? OnRaw(r.Body).Exit : null);
    }

    public Task<string> LogsAsync(string idOrName, int maxBytes, CancellationToken ct = default)
    {
        var logs = _raw.TryGetValue(idOrName, out var r) ? OnRaw(r.Body).Logs : "";
        return Task.FromResult(logs.Length > maxBytes ? logs[..maxBytes] : logs);
    }

    public Task KillAsync(string idOrName, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("kill " + idOrName);
        return Task.CompletedTask;
    }

    /// <summary>Simulate a crash.</summary>
    public void Kill(string name)
    {
        if (_byName.TryGetValue(name, out var v)) _byName[name] = (v.Spec, v.Id, false);
    }

    private string? Key(string idOrName)
        => _byName.ContainsKey(idOrName) ? idOrName : _byName.FirstOrDefault(kv => kv.Value.Id == idOrName).Key;

    private string? RawKey(string idOrName)
        => _raw.ContainsKey(idOrName) ? idOrName : _raw.FirstOrDefault(kv => kv.Value.Name == idOrName).Key;
}
