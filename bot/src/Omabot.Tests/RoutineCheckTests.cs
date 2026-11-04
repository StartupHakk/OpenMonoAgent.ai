// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Containers;
using Omabot.Doctor;
using Omabot.Egress;
using Omabot.Routines;
using Omabot.Supervision;
using Omabot.Team;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class RoutineCheckTests : IDisposable
{
    private readonly TempRoster _r = new();
    private readonly string _ws;
    public RoutineCheckTests()
    {
        _ws = Path.Combine(_r.WorkRoot, "ws");
        Directory.CreateDirectory(_ws);
    }
    public void Dispose() => _r.Dispose();

    private CheckEnvironment Env(ICommandCheckRunner? commands = null, IHostResolver? resolver = null, Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null,
        Func<CancellationToken, Task<IReadOnlyList<CheckResult>>>? doctor = null)
        => new()
        {
            Paths = _r.Paths, Clock = _r.Clock, Db = _r.Db(), AppDir = _r.AppDir, WorkspaceRoots = [_r.WorkRoot], BlockedHosts = ["x.com"],
            Commands = commands, Resolver = resolver ?? DnsResolver.Instance, Connect = connect, Doctor = doctor,
        };

    private static BotSpec Bot(params string[] hosts) => new() { Name = "Scout", Slug = "scout", AllowHosts = [.. hosts] };

    private static CheckDef Def(string kind, params (string K, string V)[] args)
    {
        var d = new CheckDef { Kind = kind };
        foreach (var (k, v) in args) d.Args[k] = v;
        return d;
    }

    [Fact(DisplayName = "file_changed: baseline first, then a change; symlinks and paths out of the workspace are refused")]
    public async Task FileChanged()
    {
        var env = Env();
        var def = Def("file_changed", ("path", "notes.md"));
        File.WriteAllText(Path.Combine(_ws, "notes.md"), "a");
        var first = await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None);
        Assert.False(first.Changed);
        var same = await Checks.RunAsync(def, Bot(), _ws, first.Fingerprint, env, CancellationToken.None);
        Assert.False(same.Changed);
        File.WriteAllText(Path.Combine(_ws, "notes.md"), "b");
        var changed = await Checks.RunAsync(def, Bot(), _ws, first.Fingerprint, env, CancellationToken.None);
        Assert.True(changed.Changed);
        Assert.Contains("notes.md changed", changed.Output, StringComparison.Ordinal);
        File.CreateSymbolicLink(Path.Combine(_ws, "link"), _r.Paths.Home);
        Assert.NotNull((await Checks.RunAsync(Def("file_changed", ("path", "link")), Bot(), _ws, null, env, CancellationToken.None)).Error);
        Assert.NotNull(Checks.Problem(Def("file_changed", ("path", "../outside"))));
        Assert.NotNull(Checks.Problem(Def("file_changed", ("path", "/etc/passwd"))));
        Assert.NotNull(Checks.Problem(Def("file_changed", ("path", "a"), ("extra", "b"))));
    }

    [Fact(DisplayName = "git_new_commits reads refs from disk (loose and packed), never runs git, and refuses repos outside the roots")]
    public async Task GitRefs()
    {
        var repo = Path.Combine(_ws, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var a = new string('a', 40);
        var b = new string('b', 40);
        File.WriteAllText(Path.Combine(repo, ".git", "refs", "heads", "main"), a + "\n");
        // A hostile repo config cannot run anything: the check never starts git.
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n\tfsmonitor = touch /tmp/should-not-run\n");
        var env = Env();
        var def = Def("git_new_commits", ("repo", "repo"), ("branch", "main"));
        var first = await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None);
        Assert.Equal((false, a), (first.Changed, first.Fingerprint));
        File.Delete(Path.Combine(repo, ".git", "refs", "heads", "main"));
        File.WriteAllText(Path.Combine(repo, ".git", "packed-refs"), $"# pack-refs with: peeled\n{b} refs/heads/main\n");
        var moved = await Checks.RunAsync(def, Bot(), _ws, first.Fingerprint, env, CancellationToken.None);
        Assert.True(moved.Changed);
        Assert.Contains("moved from aaaaaaaaaaaa to bbbbbbbbbbbb", moved.Output, StringComparison.Ordinal);
        var outside = await Checks.RunAsync(Def("git_new_commits", ("repo", "/etc"), ("branch", "main")), Bot(), _ws, null, env, CancellationToken.None);
        Assert.Contains("outside", outside.Error, StringComparison.Ordinal);
        Assert.NotNull(Checks.Problem(Def("git_new_commits", ("repo", "repo"), ("branch", "main;rm"))));
        Assert.NotNull(Checks.Problem(Def("git_new_commits", ("repo", "repo"), ("branch", "a..b"))));
        var fetch = await Checks.RunAsync(Def("git_new_commits", ("repo", "repo"), ("branch", "main"), ("fetch", "true")), Bot(), _ws, null, env, CancellationToken.None);
        Assert.Contains("supervisor", fetch.Error, StringComparison.Ordinal);
    }

    private static async Task<(WebApplication App, int Port, Func<int, int> SetStatus)> StatusServerAsync()
    {
        var status = 200;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/health", (HttpContext ctx) => Results.Json(new { state = status == 200 ? "up" : "down" }, statusCode: status));
        await app.StartAsync();
        return (app, new Uri(app.Urls.First()).Port, s => status = s);
    }

    private sealed class StubResolver(params string[] addrs) : IHostResolver
    {
        public List<string> Asked { get; } = [];
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Asked.Add(host);
            return Task.FromResult(addrs.Select(IPAddress.Parse).ToArray());
        }
    }

    [Fact(DisplayName = "http_status: allowlisted host, resolve then connect to the checked address, status and field changes")]
    public async Task HttpStatus()
    {
        var (app, port, setStatus) = await StatusServerAsync();
        await using (app)
        {
            IPAddress? connectedTo = null;
            var env = Env(resolver: new StubResolver("93.184.216.34"), connect: async (ip, _, ct) =>
            {
                connectedTo = ip;
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                return new NetworkStream(s, true);
            });
            var def = Def("http_status", ("url", "http://status.example.com/health"), ("field", "state"));
            var first = await Checks.RunAsync(def, Bot("status.example.com"), _ws, null, env, CancellationToken.None);
            Assert.Null(first.Error);
            Assert.Equal("status:200|state=up", first.Fingerprint);
            Assert.Equal(IPAddress.Parse("93.184.216.34"), connectedTo);
            setStatus(503);
            var down = await Checks.RunAsync(def, Bot("status.example.com"), _ws, first.Fingerprint, env, CancellationToken.None);
            Assert.True(down.Changed);
            Assert.Contains("503", down.Output, StringComparison.Ordinal);
            Assert.Contains("down", down.Output, StringComparison.Ordinal);
            // Not on the allowlist, blocked hosts, private and rebinding answers are refused before any connection.
            Assert.Contains("allow_hosts", (await Checks.RunAsync(def, Bot("other.example.com"), _ws, null, env, CancellationToken.None)).Error, StringComparison.Ordinal);
            Assert.Contains("blocked", (await Checks.RunAsync(Def("http_status", ("url", "https://api.x.com/")), Bot("api.x.com"), _ws, null, env, CancellationToken.None)).Error, StringComparison.Ordinal);
            connectedTo = null;
            var lan = Env(resolver: new StubResolver("10.0.0.7"), connect: env.Connect);
            Assert.Contains("private", (await Checks.RunAsync(def, Bot("status.example.com"), _ws, null, lan, CancellationToken.None)).Error, StringComparison.Ordinal);
            var rebind = Env(resolver: new StubResolver("93.184.216.34", "169.254.169.254"), connect: env.Connect);
            Assert.Contains("metadata", (await Checks.RunAsync(def, Bot("status.example.com"), _ws, null, rebind, CancellationToken.None)).Error, StringComparison.Ordinal);
            Assert.Null(connectedTo);
            Assert.NotNull(Checks.Problem(Def("http_status", ("url", "http://10.0.0.1/"))));
            Assert.NotNull(Checks.Problem(Def("http_status", ("url", "http://user:pw@example.com/"))));
        }
    }

    [Fact(DisplayName = "command checks run in a throwaway container: no network, read-only root and workspace, caps dropped, always removed")]
    public async Task CommandInContainer()
    {
        var checksDir = Path.Combine(_r.AppDir, "checks");
        Directory.CreateDirectory(checksDir);
        File.WriteAllText(Path.Combine(checksDir, "new-todo-lines.sh"), "#!/bin/bash\necho x\n");
        var engine = new FakeContainerEngine();
        var sup = new DirectSupervisor(engine, _r.Config.Bots.Network, [_r.Paths.Bots, checksDir, _r.WorkRoot]);
        var runner = new SupervisorCommandChecks(sup, _r.Config, _r.AppDir);
        var env = Env(commands: runner);
        var def = Def("command", ("run", "new-todo-lines.sh notes/todo.md"));
        engine.OnRaw = _ => (0, "3: TODO call back\n");
        var found = await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None);
        Assert.True(found.Changed, found.Error);
        Assert.Contains("TODO call back", found.Output, StringComparison.Ordinal);
        var body = Assert.Single(engine.RawCreated).Value;
        var host = body["HostConfig"]!;
        Assert.Equal("none", host["NetworkMode"]!.GetValue<string>());
        Assert.True(host["ReadonlyRootfs"]!.GetValue<bool>());
        Assert.Contains("ALL", host["CapDrop"]!.AsArray().Select(x => x!.GetValue<string>()));
        var binds = host["Binds"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains($"{checksDir}:/omabot/checks:ro", binds);
        Assert.Contains($"{_ws}:/workspace:ro", binds);
        Assert.Equal(["/bin/bash", "/omabot/checks/new-todo-lines.sh"], body["Entrypoint"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        engine.OnRaw = _ => (1, "");
        Assert.False((await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None)).Changed);
        engine.OnRaw = _ => (7, "boom");
        Assert.Contains("exited with code 7", (await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None)).Error, StringComparison.Ordinal);
        engine.OnRaw = _ => (null, "");
        Assert.Contains("limit", (await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None)).Error, StringComparison.Ordinal);
        Assert.Contains(engine.Calls, c => c.StartsWith("kill ", StringComparison.Ordinal));
        Assert.Equal(4, engine.RemovedRaw);
        // A script that is not installed, network without the egress gate, and a workspace outside the roots are refused.
        Assert.Contains("no check script", (await Checks.RunAsync(Def("command", ("run", "missing.sh")), Bot(), _ws, null, env, CancellationToken.None)).Error, StringComparison.Ordinal);
        Assert.Contains("egress gate", (await Checks.RunAsync(Def("command", ("run", "new-todo-lines.sh"), ("network", "true")), Bot(), _ws, null, env, CancellationToken.None)).Error, StringComparison.Ordinal);
        Assert.Contains("outside the allowed roots", (await runner.RunAsync("scout", "new-todo-lines.sh", [], "/etc", false, TimeSpan.FromSeconds(5), CancellationToken.None)).Error, StringComparison.Ordinal);
        Assert.NotNull(Checks.Problem(Def("command", ("run", "x.sh $(id)"))));
        Assert.NotNull(Checks.Problem(Def("command", ("run", "../x.sh"))));
        Assert.Equal(4, engine.RawCreated.Count);
    }

    [Fact(DisplayName = "doctor check: new failures are a change, the same failures stay quiet, healthy is quiet")]
    public async Task DoctorCheck()
    {
        IReadOnlyList<CheckResult> results = [new("audit_chain", CheckStatus.Fail, "broken at row 3"), new("config", CheckStatus.Ok, "fine")];
        var env = Env(doctor: _ => Task.FromResult(results));
        var def = Def("doctor");
        var first = await Checks.RunAsync(def, Bot(), _ws, null, env, CancellationToken.None);
        Assert.True(first.Changed);
        Assert.Contains("audit_chain: broken at row 3", first.Output, StringComparison.Ordinal);
        Assert.False((await Checks.RunAsync(def, Bot(), _ws, first.Fingerprint, env, CancellationToken.None)).Changed);
        results = [new("config", CheckStatus.Ok, "fine")];
        var healthy = await Checks.RunAsync(def, Bot(), _ws, first.Fingerprint, env, CancellationToken.None);
        Assert.Equal((false, "healthy"), (healthy.Changed, healthy.Fingerprint));
    }

    [Fact(DisplayName = "Address policy refuses loopback, private, link local, metadata, carrier NAT, multicast and IPv6 forms of them")]
    public void AddressPolicyRanges()
    {
        foreach (var bad in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255",
                     "::1", "::", "fe80::1", "fd00::1", "ff02::1", "::ffff:10.0.0.1", "::ffff:127.0.0.1", "64:ff9b::a00:1", "2002:a00:1::", "2001:db8::1" })
            Assert.NotNull(AddressPolicy.Refusal(IPAddress.Parse(bad)));
        foreach (var good in new[] { "93.184.216.34", "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "::ffff:93.184.216.34", "172.32.0.1", "100.128.0.1" })
            Assert.Null(AddressPolicy.Refusal(IPAddress.Parse(good)));
        Assert.True(AddressPolicy.HostAllowed("api.github.com", ["*.github.com"]));
        Assert.False(AddressPolicy.HostAllowed("github.com", ["*.github.com"]));
        Assert.True(AddressPolicy.HostAllowed("GitHub.com.", ["github.com"]));
        Assert.False(AddressPolicy.HostAllowed("evilgithub.com", ["github.com", "*.github.com"]));
        Assert.False(AddressPolicy.HostAllowed("93.184.216.34", ["93.184.216.34"]));
        Assert.True(AddressPolicy.HostBlocked("m.facebook.com", ["facebook.com"]));
        Assert.False(AddressPolicy.HostBlocked("notfacebook.com", ["facebook.com"]));
    }
}

public sealed class RoutineTeamFileTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private const string Head = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        bots:
          - name: Scout
            personality: You are Scout.
            model: flash-next
            web: { allow_hosts: [status.example.com] }
            routines:

        """;

    private async Task<(int Code, string Out, string Err)> ApplyAsync(string routines)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = e };
        var code = await new CliApp(env).RunAsync(["team", "apply", _r.TeamFile(Head + routines)]);
        return (code, o.ToString(), e.ToString());
    }

    [Theory(DisplayName = "Routine fields are checked: notify always, allow_external, past one-time, bad session, unknown check argument, foreign host")]
    [InlineData("      - { name: r, interval_minutes: 30, instructions: x, notify: always }", "hard rule 8")]
    [InlineData("      - { name: r, interval_minutes: 30, instructions: x, allow_external: true }", "hard rule 9")]
    [InlineData("      - { name: r, at: \"2026-11-01 07:00\", instructions: x }", "past")]
    [InlineData("      - { name: r, interval_minutes: 30, instructions: x, session: shared }", "session must be fresh or standing")]
    [InlineData("      - { name: r, interval_minutes: 30, instructions: x, host_exec: always }", "host_exec must be off or ask")]
    [InlineData("      - { name: r, interval_minutes: 30, instructions: x, checks: [ { file_changed: { path: a, depth: 3 } } ] }", "unknown argument")]
    [InlineData("      - { name: r, interval_minutes: 30, instructions: x, checks: [ { http_status: { url: \"https://other.example.com/\" } } ] }", "allow_hosts")]
    [InlineData("      - { name: r, cron: \"0 7 * * *\", weekdays: \"07:00\", instructions: x }", "exactly one trigger")]
    public async Task Refused(string routine, string message)
    {
        var (code, _, err) = await ApplyAsync(routine + "\n");
        Assert.Equal(Exit.Invalid, code);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Presets become cron, the doctor command maps to the doctor check, and export then apply prints No changes.")]
    public async Task PresetsAndExport()
    {
        var (code, _, err) = await ApplyAsync("""
                  - { name: a, weekdays: "07:00", instructions: x, session: standing, host_exec: ask }
                  - { name: b, monthly: "last 09:30", timezone: America/New_York, instructions: x }
                  - { name: c, yearly: "12-25 08:00", instructions: x, checks: [ { command: { run: "oma-bot doctor --json" } } ] }

            """);
        Assert.True(code == 0, err);
        var spec = new Db.BotStore(_r.Db(), _r.Clock).Get("Scout").Spec;
        Assert.Equal(["0 7 * * MON-FRI", "30 9 L * *", "0 8 25 12 *"], spec.Routines.Select(r => r.Cron ?? "").ToArray());
        Assert.Equal(("standing", "ask"), (spec.Routines[0].Session, spec.Routines[0].HostExec));
        Assert.Equal("doctor", Assert.Single(spec.Routines[2].Checks).Kind);
        var o = new StringWriter();
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "export", "--out", Path.Combine(_r.Root, "export.yaml")]));
        o.GetStringBuilder().Clear();
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", Path.Combine(_r.Root, "export.yaml")]));
        Assert.Contains("No changes.", o.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A bot may have at most 20 routines")]
    public async Task RoutineCap()
    {
        var many = string.Concat(Enumerable.Range(0, 21).Select(i => $"      - {{ name: r{i}, interval_minutes: 30, instructions: x }}\n"));
        var (code, _, err) = await ApplyAsync(many);
        Assert.Equal(Exit.Invalid, code);
        Assert.Contains("at most 20 routines", err, StringComparison.Ordinal);
    }
}
