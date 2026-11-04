// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Config;

namespace Omabot.Gateway;

/// <summary>One inference backend with its own queue (plan 7.9 node classes).</summary>
public sealed class Backend(BackendSettings settings, LaneQueue queue)
{
    public BackendSettings Settings { get; } = settings;
    public string Name => Settings.Name;
    public LaneQueue Queue { get; } = queue;
    public SlotDecision Slots { get; set; } = new(1, "unknown, one lane until measured", false);
    public IReadOnlyList<string> Labels => Settings.Labels;

    public bool HasLabels(IEnumerable<string> required)
        => required.All(r => Settings.Labels.Contains(r, StringComparer.OrdinalIgnoreCase));

    /// <summary>Upstream model for a nickname on this backend: its own mapping first, then the route default.</summary>
    public string ModelFor(ModelRoute route)
        => Settings.Models.TryGetValue(route.Nickname, out var m) ? m : route.UpstreamModel;
}

/// <summary>
/// All backends and label routing. A request goes only to a backend whose
/// labels include every label its model asks for (UNIT-GW-05). Among those,
/// the least loaded one wins.
/// </summary>
public sealed class BackendRegistry
{
    private readonly Dictionary<string, Backend> _backends = new(StringComparer.OrdinalIgnoreCase);

    public BackendRegistry(RosterConfig config, IClock clock)
    {
        foreach (var b in Effective(config))
        {
            var nc = config.Gateway.NodeClasses.FirstOrDefault(n => string.Equals(n.Name, b.NodeClass, StringComparison.OrdinalIgnoreCase));
            var configured = b.Name == "main" ? config.Gateway.Slots : null;
            var decision = SlotResolver.Decide(nc?.MeasuredSlots, configured, null);
            _backends[b.Name] = new Backend(b, new LaneQueue(decision.Slots, config.Gateway.PerBotInFlight, clock)) { Slots = decision };
        }
    }

    /// <summary>roster.json backends plus the implicit "main" backend built from gateway.upstream.</summary>
    public static List<BackendSettings> Effective(RosterConfig config)
    {
        var list = config.Gateway.Backends.ToList();
        if (!string.IsNullOrWhiteSpace(config.Gateway.Upstream) && !list.Any(b => b.Name.Equals("main", StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, new BackendSettings
            {
                Name = "main",
                Endpoint = config.Gateway.Upstream!,
                KeyFile = config.Gateway.UpstreamKeyFile,
                Labels = [.. config.Gateway.UpstreamLabels],
                NodeClass = config.Gateway.UpstreamNodeClass,
                Kind = config.Gateway.UpstreamKind,
            });
        }
        return list;
    }

    public IReadOnlyCollection<Backend> All => _backends.Values;

    public Backend? Get(string name) => _backends.GetValueOrDefault(name);

    /// <summary>Candidates for a route. A route pinned to a backend still has to carry the labels.</summary>
    public IReadOnlyList<Backend> Candidates(ModelRoute route)
    {
        IEnumerable<Backend> pool = string.IsNullOrEmpty(route.Backend) || !_backends.ContainsKey(route.Backend)
            ? _backends.Values
            : [_backends[route.Backend]];
        return [.. pool.Where(b => b.HasLabels(route.Labels))];
    }

    public Backend? Pick(ModelRoute route)
        => Candidates(route)
            .OrderBy(b => (double)(b.Queue.InFlight + b.Queue.Waiting) / Math.Max(1, b.Queue.Slots))
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .FirstOrDefault();
}
