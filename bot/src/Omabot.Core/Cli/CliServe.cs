// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Omabot.Common;
using Omabot.Gateway;
using Omabot.Secrets;
using Omabot.Supervision;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    /// <summary>
    /// `oma-bot serve`: the roster service. Phase 1 runs the pass-through
    /// gateway and the health monitor. Later phases add the API, scheduler
    /// and broker here.
    /// </summary>
    public async Task<int> ServeAsync(Args a, CancellationToken ct)
    {
        var once = a.Flag("--once");
        var dev = a.Flag("--dev");
        a.NoMore();
        var config = LoadConfig();
        if (config.Mode != "local" && Web.WebMode.Problems(config, env.Paths) is { Count: > 0 } webProblems)
        {
            foreach (var p in webProblems) env.Err.WriteLine("Web mode: " + p);
            env.Err.WriteLine("Web mode refuses to start. Fix the above, or turn it off with oma-bot web disable.");
            return Exit.Failed;
        }
        var (db, audit, notices, _) = OpenDb();
        new SecretStore(env.Paths, config, Redactor.Shared).LoadAllIntoRedactor();
        var guard = StartupSecretsProblem(config);
        if (guard is not null && !dev)
        {
            env.Err.WriteLine(guard + " The roster service will not start. Store a real key with: oma-bot secrets set inference < keyfile");
            return Exit.Failed;
        }
        var sup = RequireSupervisor();
        var launcher = Launcher(config, db, audit, notices, sup);
        var tokens = new BotTokens(env.Paths, Redactor.Shared);
        // The gateway listens on the roster network's gateway address, so the networks come first (plan 7.7).
        try
        {
            await sup.EnsureNetworksAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Containers.ContainerEngineException or HttpRequestException)
        {
            env.Err.WriteLine("The roster networks are not ready: " + Redactor.Shared.Redact(ex.Message));
            return Exit.Failed;
        }
        var app = BuildGateway(config, db, tokens, out var gateway);
        await app.StartAsync(ct).ConfigureAwait(false);
        env.Out.WriteLine($"Gateway listening on {config.Gateway.BindAddress}:{config.Gateway.Port}.");
        audit.Write("roster", "roster.serve", env.Paths.Home, $"gateway {config.Gateway.BindAddress}:{config.Gateway.Port}");
        var monitor = new HealthMonitor(db, env.Clock, launcher, env.Acp);
        var botStore = new Db.BotStore(db, env.Clock);
        var approvals = new Approvals.ApprovalStore(db, env.Clock, Redactor.Shared);
        var changes = new Routines.RoutineChanges(db, config, env.Clock, audit, approvals);
        var runtime = BuildRuntime(config, db, audit, notices, launcher, gateway);
        var broker = runtime.Pauses as Approvals.ApprovalBroker;
        if (broker is not null) broker.StopBot = (slug, token) => launcher.DownAsync(slug, token);
        var botChanges = BotChangesFor(config, db, audit, approvals);
        var rooms = new Rooms.RoomService(db, env.Clock, audit, runtime);
        var services = new RosterServices(db, env.Clock, env.Paths, new GatewayUpkeepModel(gateway, b => botStore.Find(b)?.Spec.Model), host =>
        {
            Routines.RoutineTools.Register(host, db, config, env.Clock, changes);
            Runtime.HandoffTools.Register(host, runtime, db, env.Clock, notices);
            Runtime.BotTools.Register(host, runtime, botChanges, db, config, env.Paths, env.Clock, audit);
            broker?.Register(host);
            rooms.Register(host);
        });
        services.Tools.Audit = audit;
        foreach (var line in runtime.Reconcile()) env.Out.WriteLine(line);
        var scheduler = BuildScheduler(config, db, audit, notices, sup, launcher, gateway, new Routines.AssignmentRoutineRunner(runtime, config, env.Clock));
        foreach (var line in scheduler.Recover()) env.Out.WriteLine(line);
        var sleeper = new IdleSleeper(db, env.Clock, launcher);
        var egress = new Egress.EgressClients(env.Paths, config, Redactor.Shared);
        var egressMonitor = new Egress.EgressMonitor(egress.LogPath, config, env.Clock, notices, audit);
        var desktops = new Desktops.DesktopManager(db, config, env.Paths, env.Clock, audit);
        var desktopService = new Desktops.DesktopService(desktops, sup, egress, new Desktops.DesktopSecrets(env.Paths, Redactor.Shared), config, env.Paths, audit);
        services.Tools.Add(new Api.RosterTool("desktop_endpoint", "The bot's desktop address (used by the desktop server inside the bot).", Api.RosterTool.Schema(), async (c, _, token) =>
        {
            if (botStore.Find(c.Bot) is not { } who) return Api.ToolResult.Fail("No such bot.", 404);
            var (url, problem) = await desktopService.EndpointAsync(who, c.Assignment, token).ConfigureAwait(false);
            return url is null ? Api.ToolResult.Fail(problem ?? "No desktop.", 409) : new Api.ToolResult(true, url);
        }));
        var nonce = Web.WebMode.NewNonce();
        var front = new FrontState();
        var webApp = Web.WebServer.Build(WebDepsFor(config, db, audit, notices, rooms, nonce, () => front.Ok), IPAddress.Parse(config.Api.BindAddress), config.Api.Port);
        await webApp.StartAsync(ct).ConfigureAwait(false);
        Task? frontLoop = null;
        if (config.Mode == "local") env.Out.WriteLine($"Web view on http://{config.Api.BindAddress}:{config.Api.Port}/ (sign in with oma-bot web open).");
        else
        {
            env.Out.WriteLine($"Web view for {config.Web.PublicOrigin} through the HTTPS front; sign-in opens once the front checks out.");
            if (once) await FrontCheckAsync(config, nonce, front, audit, notices, ct).ConfigureAwait(false);
            else frontLoop = Task.Run(() => FrontLoopAsync(config, nonce, front, audit, notices, ct), CancellationToken.None);
        }
        Fleet.FleetController? fleet = null;
        WebApplication? fleetApp = null;
        if (config.Placement.Mode == "fleet")
        {
            fleet = new Fleet.FleetController(db, config, env.Paths, env.Clock, audit, notices, Fleet.FleetCa.LoadOrCreate(env.Paths, env.Clock));
            if (fleet.GuardProblem() is { } fleetOff) env.Err.WriteLine(fleetOff);
            else
            {
                fleetApp = Fleet.FleetServer.Build(fleet, fleet.Ca.ServerCertificate(env.Paths, config.Fleet.ControllerName), IPAddress.Loopback, config.Fleet.ControllerPort, config.Fleet.MaxNodes);
                await fleetApp.StartAsync(ct).ConfigureAwait(false);
                env.Out.WriteLine($"Fleet controller on 127.0.0.1:{config.Fleet.ControllerPort} to {config.Fleet.ControllerPort + config.Fleet.MaxNodes} (mutual TLS; reach it only through the relay).");
            }
        }
        var gateChecked = DateTimeOffset.MinValue;
        var unreachableNoticed = false;
        var lastDiscovery = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var line in await monitor.TickAsync(ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                foreach (var line in await scheduler.TickAsync(ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                foreach (var line in changes.ApplyDecided()) env.Out.WriteLine(line);
                if (fleet is not null) foreach (var line in fleet.Tick()) env.Out.WriteLine(line);
                foreach (var line in botChanges.ApplyDecided()) env.Out.WriteLine(line);
                if (broker is not null) foreach (var line in await broker.TickAsync(ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                else approvals.ExpireDue();
                foreach (var line in rooms.Tick()) env.Out.WriteLine(line);
                foreach (var line in await runtime.TickAsync(ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                foreach (var line in await sleeper.TickAsync(ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                foreach (var line in EgressTick(config, botStore, desktops, egress)) env.Out.WriteLine(line);
                if (config.Egress.Enabled && env.Clock.UtcNow - gateChecked >= TimeSpan.FromMinutes(5))
                {
                    try
                    {
                        await sup.EnsureServiceAsync(Containers.ServiceContainerSpec.Gate(config, egress), ct).ConfigureAwait(false);
                        gateChecked = env.Clock.UtcNow;
                    }
                    catch (Exception ex) when (ex is Containers.ContainerEngineException or HttpRequestException)
                    {
                        notices.Add("error", null, "The egress gate is not running: " + Redactor.Shared.Redact(ex.Message) + " Bots and desktops have no way out until it runs.", "egress_gate_down");
                    }
                }
                foreach (var line in egressMonitor.Tick()) env.Out.WriteLine(line);
                foreach (var line in await desktopService.TickAsync(id => runtime.Store.Get(id) is { IsTerminal: false }, ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                if (gateway.InferenceUnreachable && !unreachableNoticed)
                {
                    notices.Add("error", null, "Inference is unreachable through the relay. Bots will wait; check the relay and run oma-bot doctor --online.", "inference_unreachable");
                    unreachableNoticed = true;
                }
                else if (!gateway.InferenceUnreachable) unreachableNoticed = false;
                if (env.Clock.UtcNow - lastDiscovery >= TimeSpan.FromSeconds(config.Gateway.SlotRefreshSeconds))
                {
                    await RefreshSlotsAsync(config, gateway, notices, ct).ConfigureAwait(false);
                    lastDiscovery = env.Clock.UtcNow;
                }
                WriteGatewayState(gateway);
                foreach (var line in await services.TickAsync(listen: !env.NoShimSockets, ct).ConfigureAwait(false)) env.Out.WriteLine(line);
                if (once) break;
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        // The scheduler stops first; running turns get a short time to finish (plan 6.6).
        await Task.WhenAny(Task.WhenAll(scheduler.WhenIdleAsync(), runtime.WhenIdleAsync()), Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)).ConfigureAwait(false);
        await services.Shim.StopAllAsync().ConfigureAwait(false);
        await webApp.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (fleetApp is not null) await fleetApp.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (frontLoop is not null) await frontLoop.ConfigureAwait(false);
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return Exit.Ok;
    }

    internal Web.WebDeps WebDepsFor(Config.RosterConfig config, Db.RosterDb db, Db.AuditLog audit, Db.Notices notices, Rooms.RoomService? rooms, string nonce, Func<bool> frontVerified)
    {
        var auth = WebAuthFor(config);
        var web = config.Mode != "local";
        var key = web ? Web.WebSecrets.SeedKey(Web.WebSecrets.PathOf(config, env.Paths)) : null;
        var users = new Web.UsersDb(env.Paths.UsersDb);
        var accounts = web ? new Web.WebAccounts(users, env.Clock, auth, config, audit) : null;
        var options = Web.WebMode.Options(config);
        return new()
        {
            Db = db,
            Config = config,
            Paths = env.Paths,
            Clock = env.Clock,
            Audit = audit,
            Notices = notices,
            Auth = auth,
            Options = options,
            Policy = () => ApproverPolicyFactory(config, db),
            Rooms = rooms,
            Accounts = accounts,
            TenantFor = config.Mode == "web-multi" ? Web.TenantWeb.Resolver(env.Paths, env.Clock, auth, options, accounts!, users) : null,
            SeedKey = key is null ? null : () => key,
            FrontVerified = web ? frontVerified : null,
            HealthNonce = Web.WebMode.NonceChecker(env.Paths, nonce),
        };
    }

    internal sealed class FrontState
    {
        private volatile bool _ok;
        public bool Ok { get => _ok; set => _ok = value; }
        public string? Last { get; set; } = "";
    }

    /// <summary>One HTTPS front check; sign-in opens only while it passes, and a new problem raises one notice.</summary>
    internal async Task FrontCheckAsync(Config.RosterConfig config, string nonce, FrontState front, Db.AuditLog audit, Db.Notices notices, CancellationToken ct)
    {
        var problem = await Web.WebMode.FrontProblemAsync(config, nonce, env.HttpsHandler(), ct).ConfigureAwait(false);
        front.Ok = problem is null;
        if (problem == front.Last) return;
        front.Last = problem;
        if (problem is null) audit.Write("roster", "web.front_verified", config.Web.PublicOrigin!, "sign-in open");
        else
        {
            notices.Add("error", null, $"Web mode: {problem} Sign-in is refused until the HTTPS front checks out.", "web_front:" + SecureFile.Sha256Hex(problem)[..8]);
            audit.Write("roster", "web.front_failed", config.Web.PublicOrigin!, problem);
        }
    }

    private async Task FrontLoopAsync(Config.RosterConfig config, string nonce, FrontState front, Db.AuditLog audit, Db.Notices notices, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await FrontCheckAsync(config, nonce, front, audit, notices, ct).ConfigureAwait(false);
                await Task.Delay(front.Ok ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Keeps the desktop rows and the gate's policy in step with the bots and leases.</summary>
    internal static IReadOnlyList<string> EgressTick(Config.RosterConfig config, Db.BotStore bots, Desktops.DesktopManager desktops, Egress.EgressClients egress)
    {
        var lines = new List<string>();
        var all = bots.All();
        try
        {
            lines.AddRange(desktops.Sync(all));
        }
        catch (InvalidDataException ex)
        {
            lines.Add("desktops: " + ex.Message);
        }
        var policy = egress.Build(all, desktops.All().Select(d => new Egress.DesktopClient(d.Name, desktops.AllowFrom(d))));
        if (egress.Write(policy)) lines.Add($"egress policy written ({policy.Clients.Count} clients)");
        return lines;
    }

    /// <summary>
    /// The handoff runtime as the service runs it: the driver wakes sleeping
    /// bots through the launcher, and a turn is admitted only while one of the
    /// bot's backends has an open lane (plan 7.5 "Admission").
    /// </summary>
    internal Runtime.AssignmentRuntime BuildRuntime(Config.RosterConfig config, Db.RosterDb db, Db.AuditLog audit, Db.Notices notices, BotLauncher launcher, PassThroughGateway? gateway)
    {
        var driver = new Runtime.TurnDriver(db, env.Clock, env.Paths, env.Acp, async (name, token) =>
        {
            var r = await launcher.UpAsync(name, token).ConfigureAwait(false);
            return r.Ok ? null : r.Message;
        });
        var botStore = new Db.BotStore(db, env.Clock);
        bool SlotFree(string slug)
        {
            if (gateway?.Backends is null || botStore.Find(slug) is not { } bot) return true;
            if (!ModelRoutes.Load(config, db).TryGetValue(bot.Spec.Model, out var route)) return true;
            return gateway.Backends.Candidates(route).Any(b => b.Queue.InFlight + b.Queue.Waiting < b.Queue.Slots);
        }
        var runtime = new Runtime.AssignmentRuntime(db, config, env.Paths, env.Clock, audit, notices, driver, SlotFree);
        runtime.Pauses = PauseHandlerFactory(runtime);
        return runtime;
    }

    /// <summary>The routine scheduler as the service runs it: checks in process or in throwaway containers, turns through the driver.</summary>
    internal Routines.RoutineScheduler BuildScheduler(Config.RosterConfig config, Db.RosterDb db, Db.AuditLog audit, Db.Notices notices, ISupervisorClient sup,
        BotLauncher launcher, PassThroughGateway? gateway, Routines.IRoutineRunner? runner = null)
    {
        var driver = new Runtime.TurnDriver(db, env.Clock, env.Paths, env.Acp, async (name, token) =>
        {
            var r = await launcher.UpAsync(name, token).ConfigureAwait(false);
            return r.Ok ? null : r.Message;
        });
        var blocked = db.Query("SELECT json FROM team_items WHERE kind='blocked_hosts';", r => r.GetString(0)).FirstOrDefault() is { } bj
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(bj)!.Union(config.Egress.BlockedHosts).ToList()
            : config.Egress.BlockedHosts;
        var checks = new Routines.CheckEnvironment
        {
            Paths = env.Paths,
            Clock = env.Clock,
            Db = db,
            AppDir = env.AppDir,
            WorkspaceRoots = config.WorkspaceRoots,
            BlockedHosts = blocked,
            OutputCap = config.Routines.OutputCapBytes,
            Commands = new Routines.SupervisorCommandChecks(sup, config, env.AppDir,
                slug => config.Egress.Enabled ? new Egress.EgressClients(env.Paths, config, Redactor.Shared).ProxyUrl(Egress.EgressClients.Check(slug)) : null),
            Doctor = token => new Doctor.DoctorChecks(env.Paths, env.Clock).RunAsync(new Doctor.DoctorOptions(env.AppDir, Offline: false, sup, CliEnv.SharedHttp, env.HttpsHandler()), token),
        };
        return new Routines.RoutineScheduler(db, config, env.Paths, env.Clock, audit, notices, runner ?? new Routines.AcpRoutineRunner(driver, config, env.Clock), checks,
            () => gateway?.Backends?.All.Sum(b => b.Queue.Slots) ?? 1);
    }

    internal WebApplication BuildGateway(Config.RosterConfig config, Db.RosterDb db, BotTokens tokens, out PassThroughGateway gateway, IPAddress? bindOverride = null, int? portOverride = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var ip = bindOverride ?? IPAddress.Parse(config.Gateway.BindAddress);
        var port = portOverride ?? config.Gateway.Port;
        builder.WebHost.ConfigureKestrel(k => k.Listen(ip, port));
        var app = builder.Build();
        var hashes = tokens.LoadHashes();
        var hashesAt = env.Clock.UtcNow;
        IReadOnlyDictionary<string, string> Tokens()
        {
            if (env.Clock.UtcNow - hashesAt > TimeSpan.FromSeconds(10))
            {
                hashes = tokens.LoadHashes();
                hashesAt = env.Clock.UtcNow;
            }
            return hashes;
        }
        var routes = ModelRoutes.Load(config, db);
        string? Key(string file)
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(env.Paths.Home, file);
            if (!File.Exists(path)) return null;
            var k = File.ReadAllText(path).Trim();
            Redactor.Shared.AddKnownSecret(k);
            return k;
        }
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.Gateway.RequestTimeoutSeconds) };
        var fallback = Priorities.Parse(config.Gateway.DefaultPriority);
        var events = new GatewayEventLog(Path.Combine(env.Paths.Logs, "gateway-events.log"), env.Clock);
        var gw = new PassThroughGateway(Tokens, () => routes, Key, http, env.Clock, config.Gateway.AllowPlainHttpUpstream,
            TimeSpan.FromSeconds(config.Gateway.UnreachableAfterSeconds))
        {
            Backends = new BackendRegistry(config, env.Clock),
            Contexts = new TurnContexts(db, env.Clock, fallback),
            Usage = new UsageMeter(db, env.Clock),
            FallbackPriority = fallback,
            Shaper = new RequestShaper(config.Gateway.ToolLimit, config.Gateway.SpillBytes,
                slug => Names.IsSafeSlug(slug) ? (Path.Combine(env.Paths.Bot(slug).Data, "spill"), Containers.ContainerSpec.DataDir + "/spill") : null),
            RepairToolCalls = config.Gateway.RepairToolCalls,
            OnEvent = events.Write,
            Web = new GatewayWeb(config.Gateway.SearchUpstream, config.Gateway.ScrapeUpstream, config.Gateway.UpstreamKeyFile,
                slug => new Db.BotStore(db, env.Clock).Find(slug)?.Spec,
                () => db.Query("SELECT json FROM team_items WHERE kind='blocked_hosts';", r => r.GetString(0)).SelectMany(j => System.Text.Json.JsonSerializer.Deserialize<List<string>>(j) ?? []).Union(config.Egress.BlockedHosts)),
        };
        gateway = gw;
        app.Map("/v1/{**rest}", (HttpContext ctx) => gw.HandleAsync(ctx));
        app.MapGet("/healthz", () => Results.Text(gw.InferenceUnreachable ? "inference unreachable" : "ok"));
        return app;
    }

    /// <summary>Startup secrets guard (plan 7.12 rule 8). Null when fine.</summary>
    public string? StartupSecretsProblem(Config.RosterConfig config)
    {
        foreach (var b in BackendRegistry.Effective(config))
        {
            var path = Path.IsPathRooted(b.KeyFile) ? b.KeyFile : Path.Combine(env.Paths.Home, b.KeyFile);
            if (!File.Exists(path)) continue; // a keyless upstream is allowed; doctor warns
            var problem = SecretsGuard.Problem(File.ReadAllText(path));
            if (problem is not null) return $"The key for backend {b.Name} {problem}.";
        }
        return null;
    }

    private async Task RefreshSlotsAsync(Config.RosterConfig config, PassThroughGateway gateway, Db.Notices notices, CancellationToken ct)
    {
        if (gateway.Backends is null) return;
        foreach (var b in gateway.Backends.All)
        {
            var nc = config.Gateway.NodeClasses.FirstOrDefault(n => n.Name == b.Settings.NodeClass);
            var configured = b.Name == "main" ? config.Gateway.Slots : null;
            SlotReading? reading = null;
            if (nc?.MeasuredSlots is null && (configured is null or "auto"))
                reading = await SlotDiscovery.DiscoverAsync(UpstreamHttp, b.Settings.Endpoint, ReadKey(b.Settings.KeyFile), b.Settings.Kind, ct).ConfigureAwait(false);
            var d = SlotResolver.Decide(nc?.MeasuredSlots, configured, reading);
            b.Slots = d;
            b.Queue.SetSlots(d.Slots);
            if (!d.Known)
                notices.Add("warn", null, $"Gateway backend {b.Name}: slot count unknown, using one lane until measured. Run oma-bot gateway bench --backend {b.Name}.", "slots_unknown_" + b.Name);
        }
    }

    private void WriteGatewayState(PassThroughGateway gateway)
    {
        if (gateway.Backends is null) return;
        var state = new
        {
            at = env.Clock.UtcNow.ToString("O"),
            inference_unreachable = gateway.InferenceUnreachable,
            backends = gateway.Backends.All.Select(b => new
            {
                name = b.Name,
                labels = b.Labels,
                slots = b.Slots.Slots,
                slots_source = b.Slots.Source,
                queue = b.Queue.Snapshot(),
            }),
        };
        SecureFile.EnsureDirNoClobber(env.Paths.Run);
        SecureFile.WriteAllTextAtomic(GatewayStateFile, System.Text.Json.JsonSerializer.Serialize(state, Config.RosterConfig.Json) + "\n");
    }
}
