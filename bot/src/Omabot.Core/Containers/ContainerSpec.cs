// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;
using Omabot.Common;
using Omabot.Config;
using Omabot.Render;
using Omabot.Team;

namespace Omabot.Containers;

public sealed record Mount(string Source, string Target, bool ReadOnly, string Type = "bind");

/// <summary>
/// The full description of one bot container (plan 7.1 step 6). The same
/// object renders the Docker Engine API body and the docker run arguments,
/// so the hand launch script, compose file and supervisor cannot drift
/// (INT-08 checks the compose file against this).
/// </summary>
public sealed record ContainerSpec(
    string Name,
    string Image,
    string User,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<Mount> Mounts,
    IReadOnlyList<string> Tmpfs,
    IReadOnlyDictionary<string, string> Labels,
    string HostIp,
    int HostPort,
    int ContainerPort,
    string Network,
    long MemoryBytes,
    long NanoCpus,
    long PidsLimit,
    IReadOnlyList<string> CapDrop,
    IReadOnlyList<string> SecurityOpt)
{
    public const string Workspace = "/workspace";
    public const string DataDir = "/home/agent/.openmono";
    public const string SecretsMount = "/run/secrets/bot";

    /// <summary>Paths inside the container that must be shadowed so the bot cannot reach the host bridge (plan 6.4).</summary>
    public static readonly string[] ShadowedPaths =
    [
        DataDir + "/host-bridge.json",
    ];

    public static readonly string[] TmpfsPaths =
    [
        DataDir + "/host-bridge/logs",
        DataDir + "/logs",
        "/tmp",
    ];

    public static ContainerSpec For(BotSpec spec, int hostPort, string token, RosterPaths paths, RosterConfig config, BotRenderer renderer,
        string appDir, bool shimInstalled, string? proxyUrl = null)
    {
        var bp = paths.Bot(spec.Slug);
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ACP_PORT"] = "7475",
            ["OPENMONO_API_KEY"] = token,
            ["TZ"] = spec.Timezone,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };
        if (proxyUrl is not null)
        {
            env["HTTP_PROXY"] = proxyUrl;
            env["HTTPS_PROXY"] = proxyUrl;
            env["NO_PROXY"] = "";
        }
        var mounts = new List<Mount>
        {
            new(bp.Data, DataDir, false),
            new(renderer.WorkspaceOf(spec), Workspace, false),
            new("/dev/null", ShadowedPaths[0], true),
            new(Path.Combine(appDir, "hooks"), BotRenderer.HooksMount, true),
            new(Path.Combine(bp.Root, "botcfg"), BotRenderer.BotCfgMount, true),
            new(bp.ShimSocketDir, BotRenderer.SockMount, false),
        };
        if (shimInstalled) mounts.Add(new(Path.Combine(appDir, "mcp-shim"), BotRenderer.ShimMount, true));
        if (spec.Secrets.Count > 0 || (Directory.Exists(bp.SecretsDir) && Directory.EnumerateFiles(bp.SecretsDir).Any()))
            mounts.Add(new(bp.SecretsDir, SecretsMount, true));

        return new ContainerSpec(
            Name: "oma-bot-" + spec.Slug,
            Image: config.Bots.Image,
            User: $"{config.Bots.ContainerUid}:{config.Bots.ContainerUid}",
            Command: ["--acp-only"],
            Env: env,
            Mounts: mounts,
            Tmpfs: TmpfsPaths,
            Labels: new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["oma-bot.roster"] = "1",
                ["oma-bot.bot"] = spec.Slug,
                ["oma-bot.spec"] = spec.Hash()[..16],
            },
            HostIp: "127.0.0.1",
            HostPort: hostPort,
            ContainerPort: 7475,
            Network: config.Bots.Network,
            MemoryBytes: ParseMemory(spec.LimitMemory),
            NanoCpus: (long)Math.Round(spec.LimitCpu * 1_000_000_000d),
            PidsLimit: spec.LimitPids,
            CapDrop: ["ALL"],
            SecurityOpt: ["no-new-privileges:true"]);
    }

    public static long ParseMemory(string s)
    {
        s = s.Trim().ToLowerInvariant();
        long mult = 1;
        if (s.EndsWith('g')) { mult = 1L << 30; s = s[..^1]; }
        else if (s.EndsWith('m')) { mult = 1L << 20; s = s[..^1]; }
        else if (s.EndsWith('k')) { mult = 1L << 10; s = s[..^1]; }
        return (long)(double.Parse(s, CultureInfo.InvariantCulture) * mult);
    }

    /// <summary>Problems that must stop a launch. Empty when the spec is safe.</summary>
    public IReadOnlyList<string> SafetyProblems()
    {
        var p = new List<string>();
        if (!CapDrop.Contains("ALL")) p.Add("cap_drop ALL missing");
        if (!SecurityOpt.Any(s => s.StartsWith("no-new-privileges", StringComparison.Ordinal))) p.Add("no-new-privileges missing");
        if (HostIp != "127.0.0.1") p.Add("ACP port must bind 127.0.0.1 only");
        if (Mounts.Any(m => m.Source.Contains("docker.sock", StringComparison.Ordinal) || m.Target.Contains("docker.sock", StringComparison.Ordinal)))
            p.Add("docker.sock must never be mounted");
        if (Mounts.Any(m => m.Source.EndsWith("supervisor.sock", StringComparison.Ordinal) || m.Source.EndsWith("/run", StringComparison.Ordinal)))
            p.Add("roster run folder must never be mounted");
        foreach (var shadow in ShadowedPaths)
            if (!Mounts.Any(m => m.Target == shadow && m.Source == "/dev/null")) p.Add("missing shadow mount for " + shadow);
        foreach (var t in TmpfsPaths)
            if (!Tmpfs.Contains(t)) p.Add("missing tmpfs " + t);
        if (User.StartsWith("0", StringComparison.Ordinal) || User.StartsWith("root", StringComparison.Ordinal)) p.Add("container must not run as root");
        if (PidsLimit <= 0 || MemoryBytes <= 0 || NanoCpus <= 0) p.Add("resource limits missing");
        return p;
    }

    /// <summary>Docker Engine API body for POST /containers/create.</summary>
    public JsonObject ToEngineJson()
    {
        var binds = new JsonArray();
        foreach (var m in Mounts) binds.Add($"{m.Source}:{m.Target}:{(m.ReadOnly ? "ro" : "rw")}");
        var tmpfs = new JsonObject();
        foreach (var t in Tmpfs) tmpfs[t] = "rw,nosuid,nodev,size=64m";
        var env = new JsonArray();
        foreach (var (k, v) in Env) env.Add($"{k}={v}");
        var labels = new JsonObject();
        foreach (var (k, v) in Labels) labels[k] = v;
        var port = $"{ContainerPort}/tcp";
        return new JsonObject
        {
            ["Image"] = Image,
            ["User"] = User,
            ["Cmd"] = new JsonArray([.. Command.Select(c => (JsonNode)JsonValue.Create(c)!)]),
            ["Env"] = env,
            ["Labels"] = labels,
            ["ExposedPorts"] = new JsonObject { [port] = new JsonObject() },
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = binds,
                ["Tmpfs"] = tmpfs,
                ["PortBindings"] = new JsonObject
                {
                    [port] = new JsonArray(new JsonObject { ["HostIp"] = HostIp, ["HostPort"] = HostPort.ToString(CultureInfo.InvariantCulture) }),
                },
                ["NetworkMode"] = Network,
                ["Memory"] = MemoryBytes,
                ["NanoCpus"] = NanoCpus,
                ["PidsLimit"] = PidsLimit,
                ["CapDrop"] = new JsonArray([.. CapDrop.Select(c => (JsonNode)JsonValue.Create(c)!)]),
                ["SecurityOpt"] = new JsonArray([.. SecurityOpt.Select(c => (JsonNode)JsonValue.Create(c)!)]),
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
                ["Privileged"] = false,
            },
        };
    }

    /// <summary>docker run arguments, with secret env values replaced by a marker (for display only).</summary>
    public IReadOnlyList<string> ToDockerRunArgs(bool redactEnv = true)
    {
        var a = new List<string> { "run", "-d", "--name", Name, "--user", User, "--network", Network,
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
            "--pids-limit", PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", MemoryBytes.ToString(CultureInfo.InvariantCulture),
            "--cpus", (NanoCpus / 1e9).ToString("0.###", CultureInfo.InvariantCulture),
            "-p", $"{HostIp}:{HostPort}:{ContainerPort}" };
        foreach (var (k, v) in Labels) { a.Add("--label"); a.Add($"{k}={v}"); }
        foreach (var (k, v) in Env)
        {
            a.Add("-e");
            a.Add(redactEnv && k is "OPENMONO_API_KEY" ? $"{k}=«redacted»" : $"{k}={v}");
        }
        foreach (var m in Mounts) { a.Add("-v"); a.Add($"{m.Source}:{m.Target}:{(m.ReadOnly ? "ro" : "rw")}"); }
        foreach (var t in Tmpfs) { a.Add("--tmpfs"); a.Add(t); }
        a.Add(Image);
        a.AddRange(Command);
        return a;
    }
}
