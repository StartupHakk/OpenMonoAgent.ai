// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Omabot.Common;
using Omabot.Config;
using Omabot.Gateway;
using Omabot.Secrets;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    public string GatewayStateFile => Path.Combine(env.Paths.Run, "gateway-state.json");

    /// <summary>The HTTP client used for upstream calls outside serve (slots, bench). Tests swap it.</summary>
    public HttpClient UpstreamHttp { get; init; } = new() { Timeout = TimeSpan.FromMinutes(10) };

    private string? ReadKey(string file)
    {
        var path = Path.IsPathRooted(file) ? file : Path.Combine(env.Paths.Home, file);
        if (!File.Exists(path)) return null;
        var k = File.ReadAllText(path).Trim();
        Redactor.Shared.AddKnownSecret(k);
        return k;
    }

    // ---------- gateway ----------

    private async Task<int> GatewayCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("gateway status | slots [--backend NAME] | bench [--backend NAME] [--model M] [--levels 1,2,4] [--rounds N] [--max-tokens N] [--save]");
        var config = LoadConfig();
        switch (sub)
        {
            case "status":
                a.NoMore();
                if (!File.Exists(GatewayStateFile)) { env.Out.WriteLine("The roster service is not running (no gateway state). Start it with oma-bot serve."); return Exit.Ok; }
                env.Out.WriteLine(File.ReadAllText(GatewayStateFile).TrimEnd());
                return Exit.Ok;
            case "slots":
            {
                var only = a.Option("--backend");
                a.NoMore();
                foreach (var b in BackendRegistry.Effective(config).Where(b => only is null || b.Name == only))
                {
                    var reading = await SlotDiscovery.DiscoverAsync(UpstreamHttp, b.Endpoint, ReadKey(b.KeyFile), b.Kind, ct).ConfigureAwait(false);
                    var nc = config.Gateway.NodeClasses.FirstOrDefault(n => n.Name == b.NodeClass);
                    var d = SlotResolver.Decide(nc?.MeasuredSlots, b.Name == "main" ? config.Gateway.Slots : null, reading);
                    env.Out.WriteLine($"{b.Name}: server says {(reading.Slots?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} ({reading.Source}{(reading.Problem is null ? "" : "; " + reading.Problem)}); gateway uses {d.Slots} ({d.Source}).");
                }
                return Exit.Ok;
            }
            case "bench":
                return await BenchCmd(a, config, ct).ConfigureAwait(false);
            default:
                throw new ArgsException("Usage: oma-bot gateway status | slots | bench");
        }
    }

    private async Task<int> BenchCmd(Args a, RosterConfig config, CancellationToken ct)
    {
        var name = a.Option("--backend") ?? "main";
        var model = a.Option("--model");
        var levels = (a.Option("--levels") ?? "1,2,4").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) && n is >= 1 and <= 16 ? n : throw new ArgsException("--levels takes numbers 1 to 16, like 1,2,4.")).ToList();
        var rounds = int.Parse(a.Option("--rounds") ?? "2", CultureInfo.InvariantCulture);
        var maxTokens = int.Parse(a.Option("--max-tokens") ?? "200", CultureInfo.InvariantCulture);
        var save = a.Flag("--save");
        a.NoMore();
        var b = BackendRegistry.Effective(config).FirstOrDefault(x => x.Name == name)
            ?? throw new ArgsException($"No backend named {name}. Set gateway.upstream or gateway.backends in roster.json.");
        var policy = UpstreamPolicy.Check(b.Endpoint, config.Gateway.AllowPlainHttpUpstream);
        if (policy is not null) throw new ArgsException(policy);
        model ??= b.Models.Values.FirstOrDefault() ?? FirstTeamModel()
            ?? throw new ArgsException("Pass --model with the upstream model name.");
        var key = ReadKey(b.KeyFile);
        var discovery = await SlotDiscovery.DiscoverAsync(UpstreamHttp, b.Endpoint, key, b.Kind, ct).ConfigureAwait(false);
        env.Out.WriteLine($"Benchmarking {name} ({model}) at concurrency {string.Join(", ", levels)}, {rounds} rounds each. This sends real requests to the inference server.");
        var report = await new GatewayBench(UpstreamHttp, b.Endpoint, key, model, env.Clock) { MaxTokens = maxTokens }
            .RunAsync(name, levels, rounds, discovery, ct).ConfigureAwait(false);
        var md = report.ToMarkdown();
        var reports = Path.Combine(env.Paths.Home, "reports");
        SecureFile.EnsureDirNoClobber(reports);
        var file = Path.Combine(reports, $"bench-{name}-{env.Clock.UtcNow:yyyyMMdd-HHmmss}.md");
        SecureFile.WriteAllTextAtomic(file, md);
        env.Out.WriteLine(md);
        env.Out.WriteLine($"Report written to {file}.");
        if (save)
        {
            if (report.R1 <= 0) { env.Err.WriteLine("No successful streams; nothing saved."); return Exit.TurnFailed; }
            var ncName = b.NodeClass ?? (name == "main" ? "main" : name);
            var nc = config.Gateway.NodeClasses.FirstOrDefault(n => n.Name == ncName);
            if (nc is null) { nc = new NodeClass { Name = ncName }; config.Gateway.NodeClasses.Add(nc); }
            nc.Model = model;
            nc.Backend = b.Kind;
            nc.MeasuredSlots = discovery.Slots;
            nc.TokensPerSecond = Math.Round(report.R1, 1);
            nc.PromptTokensPerSecond = Math.Round(report.PromptTokensPerSecond, 0);
            nc.LatencySeconds = Math.Round(report.LatencySeconds, 3);
            nc.SpeedFactors = report.Levels.ToDictionary(l => l.Concurrency.ToString(CultureInfo.InvariantCulture), l => report.SpeedFactor(l.Concurrency));
            nc.MeasuredOn = env.Clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (name == "main" && !config.Gateway.Backends.Any(x => x.Name == "main")) config.Gateway.UpstreamNodeClass = ncName;
            else if (config.Gateway.Backends.FirstOrDefault(x => x.Name == name) is { } cb) cb.NodeClass = ncName;
            config.Save(env.Paths);
            var (db, audit, _, _) = OpenDb();
            audit.Write("person", "gateway.bench.save", name, $"node class {ncName} slots {discovery.Slots?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} r1 {nc.TokensPerSecond}");
            env.Out.WriteLine($"Saved measured numbers to node class {ncName} in roster.json.{(discovery.Slots is null ? " Slots stay unknown because the server did not report them." : "")}");
        }
        return Exit.Ok;
    }

    private string? FirstTeamModel()
    {
        if (!File.Exists(env.Paths.Db)) return null;
        var (db, _, _, _) = OpenDb();
        return ModelRoutes.Load(new RosterConfig(), db).Values.Select(r => r.UpstreamModel).FirstOrDefault();
    }

    // ---------- secrets ----------

    private Task<int> SecretsCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("secrets set NAME [--bot BOT] [--file PATH] | list [--bot BOT] | rm NAME [--bot BOT] | rotate-bot BOT");
        if (a.Option("--value") is not null) throw new ArgsException("Secrets are never taken from the command line. Use --file PATH or standard input.");
        var config = LoadConfig();
        var store = new SecretStore(env.Paths, config, Redactor.Shared);
        var botName = a.Option("--bot");
        string? slug = null;
        if (botName is not null)
        {
            var (_, _, _, bots) = OpenDb();
            slug = bots.Get(botName).Slug;
        }
        try
        {
            switch (sub)
            {
                case "set":
                {
                    var file = a.Option("--file");
                    var name = a.Next("secrets set NAME [--bot BOT] [--file PATH]");
                    a.NoMore();
                    var value = file is not null
                        ? (File.Exists(file) ? File.ReadAllText(file) : throw new ArgsException($"No file at {file}."))
                        : env.ReadStdin() ?? throw new ArgsException("Pipe the secret on standard input or pass --file PATH.");
                    var r = store.Set(name, value, slug);
                    var (_, audit, _, _) = OpenDb();
                    audit.Write("person", "secret.set", slug is null ? name : $"{slug}/{name}", "value not logged");
                    env.Out.WriteLine($"Stored {name}{(slug is null ? "" : " for " + botName)} (0600). {r.Applies[..1].ToUpperInvariant()}{r.Applies[1..]}.");
                    return Task.FromResult(Exit.Ok);
                }
                case "list":
                    a.NoMore();
                    var list = store.List(slug);
                    if (list.Count == 0) env.Out.WriteLine("No secrets stored.");
                    foreach (var s in list) env.Out.WriteLine($"{s.Name,-24} mode {s.Mode}  {s.Bytes} bytes  updated {s.Updated:yyyy-MM-dd HH:mm} UTC");
                    return Task.FromResult(Exit.Ok);
                case "rm":
                {
                    var name = a.Next("secrets rm NAME [--bot BOT]");
                    a.NoMore();
                    var removed = store.Remove(name, slug);
                    if (removed)
                    {
                        var (_, audit, _, _) = OpenDb();
                        audit.Write("person", "secret.rm", slug is null ? name : $"{slug}/{name}", "");
                    }
                    env.Out.WriteLine(removed ? $"Removed {name}." : $"No secret named {name}.");
                    return Task.FromResult(removed ? Exit.Ok : Exit.Failed);
                }
                case "rotate-bot":
                {
                    var who = a.Next("secrets rotate-bot BOT");
                    a.NoMore();
                    var (_, audit, _, bots) = OpenDb();
                    var row = bots.Get(who);
                    new BotTokens(env.Paths, Redactor.Shared).Rotate(row.Slug);
                    audit.Write("person", "secret.rotate_bot_token", row.Slug, "value not logged");
                    env.Out.WriteLine($"New gateway token for {row.Name}. The old one stops working within 10 seconds. Restart to apply: oma-bot down {row.Name} && oma-bot up {row.Name}");
                    return Task.FromResult(Exit.Ok);
                }
                default:
                    throw new ArgsException("Usage: oma-bot secrets set | list | rm | rotate-bot");
            }
        }
        catch (SecretException ex)
        {
            env.Err.WriteLine(ex.Message);
            return Task.FromResult(Exit.Failed);
        }
    }

    // ---------- usage ----------

    private Task<int> UsageCmd(Args a, CancellationToken ct)
    {
        var days = int.Parse(a.Option("--days") ?? "1", CultureInfo.InvariantCulture);
        var botName = a.Option("--bot");
        var json = a.Flag("--json");
        a.NoMore();
        var (db, _, _, bots) = OpenDb();
        var slug = botName is null ? null : bots.Get(botName).Slug;
        var rows = new UsageMeter(db, env.Clock).Summary(TimeSpan.FromDays(days), slug);
        if (json) { env.Out.WriteLine(JsonSerializer.Serialize(rows, RosterConfig.Json)); return Task.FromResult(Exit.Ok); }
        if (rows.Count == 0) { env.Out.WriteLine($"No model calls in the last {days} day(s)."); return Task.FromResult(Exit.Ok); }
        env.Out.WriteLine($"{"BOT",-16} {"ROUTINE",-16} {"CALLS",6} {"PROMPT",10} {"OUTPUT",10} {"QUEUE P95",10}");
        foreach (var r in rows)
            env.Out.WriteLine($"{r.Bot,-16} {r.Routine ?? "-",-16} {r.Calls,6} {r.PromptTokens,10} {r.CompletionTokens,10} {r.QueueMsP95 / 1000.0,9:0.0}s{(r.Estimated > 0 ? "  (some output counts estimated)" : "")}");
        return Task.FromResult(Exit.Ok);
    }
}
