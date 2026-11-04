// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Common;
using Omabot.Config;
using Omabot.Egress;

namespace Omabot.Containers;

public sealed record NetworkAttach(string Network, IReadOnlyList<string> Aliases);

public sealed record PortMap(int HostPort, int ContainerPort);

/// <summary>
/// A roster service container: the egress gate or a desktop (plan 7.7). The
/// supervisor checks it again before Docker sees it: only roster networks,
/// published ports on 127.0.0.1 only, mounts under the roster home, never
/// the Docker socket, never root, all capabilities dropped, limits set, and
/// no bot or host credential in its environment.
/// </summary>
public sealed partial record ServiceContainerSpec(
    string Name,
    string Kind,
    string Image,
    string User,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<Mount> Mounts,
    IReadOnlyList<string> Tmpfs,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<NetworkAttach> Networks,
    IReadOnlyList<PortMap> Ports,
    long MemoryBytes,
    long NanoCpus,
    long PidsLimit,
    bool ReadOnlyRoot,
    long ShmBytes)
{
    public const string GateName = "oma-roster-egress";
    public const string DesktopPrefix = "oma-desktop-";

    [GeneratedRegex("^oma-desktop-[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex DesktopName();

    public static bool IsServiceName(string name) => name == GateName || DesktopName().IsMatch(name);

    private static readonly string[] ForbiddenEnv = ["OPENMONO_API_KEY", "OPENMONO_HOST_INVITE", "OPENMONO_HOST_PAIR_SECRET", "DOCKER_HOST"];

    public IReadOnlyList<string> SafetyProblems(RosterConfig config, IReadOnlyCollection<string> mountRoots)
    {
        var p = new List<string>();
        var bots = Egress.Networks.Bots(config).Name;
        var desk = Egress.Networks.Desktops(config).Name;
        var egress = Egress.Networks.Egress(config).Name;
        switch (Kind)
        {
            case "gate":
                if (Name != GateName) p.Add("the gate must be named " + GateName);
                if (Networks.Count == 0 || Networks[0].Network != egress) p.Add("the gate's first network must be " + egress);
                if (Networks.Skip(1).Any(n => n.Network != bots && n.Network != desk)) p.Add("the gate may join only the roster networks");
                if (Ports.Count > 0) p.Add("the gate publishes no ports");
                break;
            case "desktop":
                if (!DesktopName().IsMatch(Name)) p.Add("a desktop must be named oma-desktop-<name>");
                if (Networks.Count != 1 || Networks[0].Network != desk) p.Add("a desktop joins only " + desk);
                foreach (var port in Ports)
                    if (port.HostPort < config.Desktops.PortBase || port.HostPort >= config.Desktops.PortBase + config.Desktops.Max)
                        p.Add($"desktop port {port.HostPort} is outside {config.Desktops.PortBase} to {config.Desktops.PortBase + config.Desktops.Max - 1}");
                break;
            default:
                p.Add("unknown service kind " + Kind);
                break;
        }
        foreach (var m in Mounts)
        {
            if (m.Source.Contains("docker.sock", StringComparison.Ordinal) || m.Target.Contains("docker.sock", StringComparison.Ordinal)) p.Add("docker.sock must never be mounted");
            var full = Path.GetFullPath(m.Source);
            if (!mountRoots.Any(r => full == Path.GetFullPath(r) || full.StartsWith(Path.GetFullPath(r).TrimEnd('/') + "/", StringComparison.Ordinal)))
                p.Add($"mount {m.Source} is outside the roster home");
            if (full.EndsWith("/run", StringComparison.Ordinal) || full.Contains("supervisor", StringComparison.Ordinal)) p.Add("the roster run folder must never be mounted");
        }
        if (User.StartsWith('0') || User.StartsWith("root", StringComparison.Ordinal)) p.Add("service containers never run as root");
        if (MemoryBytes <= 0 || NanoCpus <= 0 || PidsLimit <= 0) p.Add("resource limits missing");
        if (Env.Keys.Any(k => ForbiddenEnv.Contains(k))) p.Add("a bot or host credential is in the environment");
        if (!Labels.TryGetValue("oma-bot.roster", out var r) || r != "1") p.Add("roster label missing");
        return p;
    }

    public string Hash() => SecureFile.Sha256Hex(ToEngineJson().ToJsonString())[..16];

    /// <summary>Docker Engine API body for POST /containers/create.</summary>
    public JsonObject ToEngineJson()
    {
        var binds = new JsonArray();
        foreach (var m in Mounts) binds.Add($"{m.Source}:{m.Target}:{(m.ReadOnly ? "ro" : "rw")}");
        var tmpfs = new JsonObject();
        foreach (var t in Tmpfs) tmpfs[t] = "rw,nosuid,nodev,size=64m";
        var env = new JsonArray();
        foreach (var (k, v) in Env.OrderBy(kv => kv.Key, StringComparer.Ordinal)) env.Add($"{k}={v}");
        var labels = new JsonObject();
        foreach (var (k, v) in Labels.OrderBy(kv => kv.Key, StringComparer.Ordinal)) labels[k] = v;
        var exposed = new JsonObject();
        var bindings = new JsonObject();
        foreach (var port in Ports)
        {
            var key = $"{port.ContainerPort}/tcp";
            exposed[key] = new JsonObject();
            bindings[key] = new JsonArray(new JsonObject { ["HostIp"] = "127.0.0.1", ["HostPort"] = port.HostPort.ToString(CultureInfo.InvariantCulture) });
        }
        var first = Networks.Count > 0 ? Networks[0] : new NetworkAttach("none", []);
        var body = new JsonObject
        {
            ["Image"] = Image,
            ["User"] = User,
            ["Env"] = env,
            ["Labels"] = labels,
            ["ExposedPorts"] = exposed,
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = binds,
                ["Tmpfs"] = tmpfs,
                ["PortBindings"] = bindings,
                ["NetworkMode"] = first.Network,
                ["Memory"] = MemoryBytes,
                ["NanoCpus"] = NanoCpus,
                ["PidsLimit"] = PidsLimit,
                ["CapDrop"] = new JsonArray("ALL"),
                ["SecurityOpt"] = new JsonArray("no-new-privileges:true"),
                ["ReadonlyRootfs"] = ReadOnlyRoot,
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
                ["Privileged"] = false,
            },
            ["NetworkingConfig"] = new JsonObject
            {
                ["EndpointsConfig"] = new JsonObject { [first.Network] = new JsonObject { ["Aliases"] = new JsonArray([.. first.Aliases.Select(a => (JsonNode)JsonValue.Create(a)!)]) } },
            },
        };
        if (Command.Count > 0) body["Cmd"] = new JsonArray([.. Command.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        if (ShmBytes > 0) ((JsonObject)body["HostConfig"]!)["ShmSize"] = ShmBytes;
        return body;
    }

    /// <summary>The egress gate's container (plan 7.7): on the egress network, reachable as "egress" on the roster networks.</summary>
    public static ServiceContainerSpec Gate(RosterConfig config, EgressClients clients)
    {
        var spec = new ServiceContainerSpec(
            GateName, "gate", config.Egress.Image, "1000:1000", [],
            new Dictionary<string, string>
            {
                ["OMABOT_EGRESS_POLICY"] = "/etc/omabot-egress/policy.json",
                ["OMABOT_EGRESS_LOG"] = "/var/log/omabot-egress/egress.log",
                ["OMABOT_EGRESS_LISTEN"] = "0.0.0.0:" + config.Egress.Port.ToString(CultureInfo.InvariantCulture),
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
            [new Mount(clients.PolicyDir, "/etc/omabot-egress", true), new Mount(clients.LogDir, "/var/log/omabot-egress", false)],
            ["/tmp"],
            new Dictionary<string, string> { ["oma-bot.roster"] = "1", ["oma-bot.role"] = "egress" },
            [
                new NetworkAttach(Egress.Networks.Egress(config).Name, []),
                new NetworkAttach(Egress.Networks.Bots(config).Name, [config.Egress.Host]),
                new NetworkAttach(Egress.Networks.Desktops(config).Name, [config.Egress.Host]),
            ],
            [], 256L << 20, 500_000_000, 256, true, 0);
        return spec with { Labels = new Dictionary<string, string>(spec.Labels) { ["oma-bot.spec"] = spec.Hash() } };
    }
}
