// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Config;

namespace Omabot.Containers;

/// <summary>
/// A throwaway container for one routine command check (plan 7.4): a script
/// from bot/checks runs with no network unless allowed, a read-only root file
/// system, the bot workspace mounted read only (read write only for a git
/// fetch), all capabilities dropped and a time limit. Checked again by the
/// supervisor before Docker sees it.
/// </summary>
public sealed partial record CheckContainerSpec(
    string Name,
    string Image,
    string User,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Command,
    IReadOnlyList<Mount> Mounts,
    string Network,
    IReadOnlyDictionary<string, string> Env,
    long MemoryBytes,
    long NanoCpus,
    long PidsLimit,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, string> Labels)
{
    public const string ChecksMount = "/omabot/checks";
    public const string WorkspaceMount = "/workspace";

    [GeneratedRegex("^oma-check-[a-z0-9-]{1,60}-[0-9a-f]{8}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^/omabot/checks/[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ScriptPath();

    [GeneratedRegex("^[A-Za-z0-9._/=:,+@-]{1,200}$")]
    private static partial Regex Arg();

    public static CheckContainerSpec For(string botSlug, string script, IReadOnlyList<string> args, string checksDir, string workspace, bool writableWorkspace,
        string network, IReadOnlyDictionary<string, string>? env, RosterConfig config, TimeSpan timeout)
        => new(
            Name: $"oma-check-{botSlug}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}",
            Image: config.Bots.Image,
            User: $"{config.Bots.ContainerUid}:{config.Bots.ContainerUid}",
            Entrypoint: ["/bin/bash", $"{ChecksMount}/{script}"],
            Command: [.. args],
            Mounts: [new Mount(checksDir, ChecksMount, true), new Mount(workspace, WorkspaceMount, !writableWorkspace)],
            Network: network,
            Env: env ?? new Dictionary<string, string>(),
            MemoryBytes: 256L << 20,
            NanoCpus: 500_000_000,
            PidsLimit: 128,
            TimeoutSeconds: (int)Math.Clamp(timeout.TotalSeconds, 1, 600),
            Labels: new SortedDictionary<string, string>(StringComparer.Ordinal) { ["oma-bot.check"] = "1", ["oma-bot.bot"] = botSlug });

    /// <summary>Problems that refuse the run. Mount sources must sit under one of the allowed roots.</summary>
    public IReadOnlyList<string> SafetyProblems(IReadOnlyCollection<string> allowedRoots, string rosterNetwork)
    {
        var p = new List<string>();
        if (!NamePattern().IsMatch(Name)) p.Add("check container name must be oma-check-<bot>-<8 hex>");
        if (Entrypoint.Count != 2 || Entrypoint[0] != "/bin/bash" || !ScriptPath().IsMatch(Entrypoint[1]) || Entrypoint[1].Contains("..", StringComparison.Ordinal))
            p.Add("entrypoint must be /bin/bash and one script from /omabot/checks");
        if (Command.Any(a => !Arg().IsMatch(a))) p.Add("script arguments have characters that are not allowed");
        if (Network != "none" && Network != rosterNetwork) p.Add("a check runs with no network, or on the roster network behind the egress gate");
        if (Network == rosterNetwork && !(Env.TryGetValue("HTTPS_PROXY", out var proxy) && proxy.Length > 0)) p.Add("a check with network must go through the egress gate proxy");
        if (User.StartsWith('0') || User.StartsWith("root", StringComparison.Ordinal)) p.Add("check container must not run as root");
        if (MemoryBytes <= 0 || NanoCpus <= 0 || PidsLimit <= 0) p.Add("resource limits missing");
        if (TimeoutSeconds is < 1 or > 600) p.Add("time limit must be 1 to 600 seconds");
        foreach (var m in Mounts)
        {
            if (m.Source.Contains("docker.sock", StringComparison.Ordinal) || m.Target.Contains("docker.sock", StringComparison.Ordinal)) p.Add("docker.sock must never be mounted");
            if (m.Target is not (ChecksMount or WorkspaceMount)) p.Add($"unexpected mount target {m.Target}");
            if (m.Target == ChecksMount && !m.ReadOnly) p.Add("the checks folder must be read only");
            var full = Path.GetFullPath(m.Source);
            if (full != m.Source.TrimEnd('/') || m.Source.Contains("/../", StringComparison.Ordinal)) p.Add($"mount source {m.Source} is not a plain absolute path");
            else if (!allowedRoots.Any(r => Team.TeamResolver.IsUnder(full, r))) p.Add($"mount source {m.Source} is outside the allowed roots");
            if (Common.SecureFile.HasSymlinkSegment(full)) p.Add($"mount source {m.Source} has a symlink");
        }
        if (Mounts.Count(m => m.Target == ChecksMount) != 1) p.Add("the checks folder must be mounted once");
        return p;
    }

    public JsonObject ToEngineJson()
    {
        var binds = new JsonArray();
        foreach (var m in Mounts) binds.Add($"{m.Source}:{m.Target}:{(m.ReadOnly ? "ro" : "rw")}");
        var env = new JsonArray();
        foreach (var (k, v) in Env) env.Add($"{k}={v}");
        var labels = new JsonObject();
        foreach (var (k, v) in Labels) labels[k] = v;
        return new JsonObject
        {
            ["Image"] = Image,
            ["User"] = User,
            ["Entrypoint"] = new JsonArray([.. Entrypoint.Select(e => (JsonNode)JsonValue.Create(e)!)]),
            ["Cmd"] = new JsonArray([.. Command.Select(c => (JsonNode)JsonValue.Create(c)!)]),
            ["Env"] = env,
            ["Labels"] = labels,
            ["WorkingDir"] = WorkspaceMount,
            ["NetworkDisabled"] = Network == "none",
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = binds,
                ["Tmpfs"] = new JsonObject { ["/tmp"] = "rw,nosuid,nodev,noexec,size=16m" },
                ["NetworkMode"] = Network,
                ["Memory"] = MemoryBytes,
                ["NanoCpus"] = NanoCpus,
                ["PidsLimit"] = PidsLimit,
                ["CapDrop"] = new JsonArray("ALL"),
                ["SecurityOpt"] = new JsonArray("no-new-privileges:true"),
                ["ReadonlyRootfs"] = true,
                ["Privileged"] = false,
                ["AutoRemove"] = false,
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
            },
        };
    }

    public override string ToString() => $"{Name} {string.Join(' ', Entrypoint)} {string.Join(' ', Command)} ({TimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s)";
}
