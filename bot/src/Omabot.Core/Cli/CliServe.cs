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
        var app = BuildGateway(config, db, tokens, out var gateway);
        await app.StartAsync(ct).ConfigureAwait(false);
        env.Out.WriteLine($"Gateway listening on {config.Gateway.BindAddress}:{config.Gateway.Port}.");
        audit.Write("roster", "roster.serve", env.Paths.Home, $"gateway {config.Gateway.BindAddress}:{config.Gateway.Port}");
        var monitor = new HealthMonitor(db, env.Clock, launcher, env.Acp);
        var botStore = new Db.BotStore(db, env.Clock);
        var services = new RosterServices(db, env.Clock, env.Paths, new GatewayUpkeepModel(gateway, b => botStore.Find(b)?.Spec.Model));
        var unreachableNoticed = false;
        var lastDiscovery = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var line in await monitor.TickAsync(ct).ConfigureAwait(false)) env.Out.WriteLine(line);
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
        await services.Shim.StopAllAsync().ConfigureAwait(false);
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        return Exit.Ok;
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
        var gw = new PassThroughGateway(Tokens, () => routes, Key, http, env.Clock, config.Gateway.AllowPlainHttpUpstream,
            TimeSpan.FromSeconds(config.Gateway.UnreachableAfterSeconds))
        {
            Backends = new BackendRegistry(config, env.Clock),
            Contexts = new TurnContexts(db, env.Clock, fallback),
            Usage = new UsageMeter(db, env.Clock),
            FallbackPriority = fallback,
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
