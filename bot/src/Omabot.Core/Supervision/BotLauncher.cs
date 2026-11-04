// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Acp;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Render;
using Omabot.Secrets;

namespace Omabot.Supervision;

public sealed record LaunchResult(bool Ok, string Slug, string State, string Message);

/// <summary>Options that are not in roster.json.</summary>
public sealed record LaunchOptions(string AppDir, TimeSpan ReadyTimeout, TimeSpan PollInterval)
{
    public static LaunchOptions Default(string appDir) => new(appDir, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1));
}

/// <summary>
/// The launch recipe (plan 7.1): enabled check, token, folders then files,
/// container spec and safety check, create and start through the
/// supervisor, wait for discovery, then the standing session.
/// </summary>
public sealed class BotLauncher(
    RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit, Notices notices,
    ISupervisorClient supervisor, Func<int, AcpClient> acpFor, LaunchOptions options)
{
    private readonly BotStore _bots = new(db, clock);
    private readonly BotRenderer _renderer = new(paths, config);

    public BotRenderer Renderer => _renderer;

    public RosterPaths Paths => paths;

    public RosterDb Db => db;

    public string GatewayUrl => $"http://{config.Gateway.BindAddress}:{config.Gateway.Port}/v1";

    public bool ShimInstalled => File.Exists(Path.Combine(options.AppDir, "mcp-shim", "run"));

    public RenderContext ContextFor(BotRow row)
    {
        var blocked = db.Query("SELECT json FROM team_items WHERE kind='blocked_hosts';", r => r.GetString(0)).FirstOrDefault();
        var hosts = blocked is null ? config.Egress.BlockedHosts : System.Text.Json.JsonSerializer.Deserialize<List<string>>(blocked)!.Union(config.Egress.BlockedHosts).ToList();
        int? ctx = null;
        var model = db.Query("SELECT json FROM team_items WHERE kind='model' AND name=$n;", r => r.GetString(0), ("$n", row.Spec.Model)).FirstOrDefault();
        if (model is not null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(model);
            if (doc.RootElement.TryGetProperty("context_size", out var cs) && cs.ValueKind == System.Text.Json.JsonValueKind.Number) ctx = cs.GetInt32();
        }
        var mem = Memory.MemoryStores.For(db, clock, paths, row.Spec);
        if (mem is null) return new RenderContext(GatewayUrl, ShimInstalled, ctx, hosts);
        return new RenderContext(GatewayUrl, ShimInstalled, ctx, hosts, mem.Load().Block, mem.TopicIndex());
    }

    public async Task<LaunchResult> UpAsync(string name, CancellationToken ct = default)
    {
        var row = _bots.Find(name);
        if (row is null) return new LaunchResult(false, name, "missing", $"No bot named \"{name}\".");
        if (!row.Enabled) return new LaunchResult(false, row.Slug, BotStates.Disabled, $"{row.Name} is disabled: {row.DisabledReason}. Run oma-bot enable {row.Name}.");

        var failures = db.Query("SELECT at FROM bot_failures WHERE bot_id=$b ORDER BY at DESC LIMIT 10;",
            r => DateTimeOffset.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture), ("$b", row.Id));
        var decision = Backoff.Next(failures, clock.UtcNow);
        if (decision.Disable)
        {
            Disable(row, $"{decision.FailuresInWindow} failures in 30 minutes");
            return new LaunchResult(false, row.Slug, BotStates.Disabled, $"{row.Name} failed {decision.FailuresInWindow} times in 30 minutes and is now disabled.");
        }

        // SEC-14: the agent image must be the one a person accepted; a changed image is never started on its own.
        var imageId = await supervisor.ImageIdAsync(config.Bots.Image, ct).ConfigureAwait(false);
        if (ImageProblem(config, imageId) is { } imageProblem)
        {
            audit.Write("roster", "bot.up_refused", row.Name, imageProblem);
            return new LaunchResult(false, row.Slug, row.State, imageProblem);
        }

        _bots.SetState(row.Slug, BotStates.Starting);
        try
        {
            var token = new BotTokens(paths, Redactor.Shared).Ensure(row.Slug);
            var secretProblems = BotSecrets.Materialize(row.Spec, paths, Redactor.Shared);
            if (secretProblems.Count > 0) throw new ContainerEngineException("Bot secrets are not ready: " + string.Join("; ", secretProblems));
            var rendered = _renderer.Render(row.Spec, ContextFor(row));
            _bots.SetRenderedHash(row.Slug, rendered.OpenMonoHash);
            var invite = row.Spec.HostExec ? await HostInviteForAsync(row, ct).ConfigureAwait(false) : null;
            var spec = ContainerSpec.For(row.Spec, row.Port, token, paths, config, _renderer, options.AppDir, ShimInstalled, EgressProxyFor?.Invoke(row.Slug), invite);
            var problems = spec.SafetyProblems();
            if (problems.Count > 0) throw new ContainerEngineException("Unsafe container spec: " + string.Join("; ", problems));

            var id = await supervisor.EnsureRunningAsync(spec, ct).ConfigureAwait(false);
            _bots.SetContainer(row.Slug, id, imageId);

            var acp = acpFor(row.Port);
            var deadline = clock.UtcNow + options.ReadyTimeout;
            AcpDiscovery? disc = null;
            while (true)
            {
                disc = await acp.DiscoveryAsync(ct).ConfigureAwait(false);
                if (disc is { Status: "ready" }) break;
                if (clock.UtcNow >= deadline) throw new TimeoutException($"{row.Name} did not answer discovery within {options.ReadyTimeout.TotalSeconds:0} seconds.");
                await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
            }

            var session = _bots.GetSession(row.Id);
            if (session is null || await acp.GetSessionAsync(session, ct).ConfigureAwait(false) is null)
            {
                session = await acp.CreateSessionAsync(row.Spec.Model, ct).ConfigureAwait(false);
                _bots.SetSession(row.Id, session);
            }
            _bots.SetState(row.Slug, BotStates.Running);
            _bots.Touch(row.Slug);
            audit.Write("roster", "bot.up", row.Name, $"container {id[..Math.Min(12, id.Length)]} port {row.Port}");
            return new LaunchResult(true, row.Slug, BotStates.Running, $"{row.Name} is running on 127.0.0.1:{row.Port}.");
        }
        catch (Exception ex) when (ex is ContainerEngineException or TimeoutException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var msg = Redactor.Shared.Redact(ex.Message);
            _bots.RecordFailure(row.Id, msg);
            _bots.SetState(row.Slug, BotStates.Failed, msg);
            audit.Write("roster", "bot.up_failed", row.Name, msg);
            var after = Backoff.Next(failures.Append(clock.UtcNow), clock.UtcNow);
            if (after.Disable)
            {
                Disable(row, $"{after.FailuresInWindow} failures in 30 minutes: {msg}");
                return new LaunchResult(false, row.Slug, BotStates.Disabled, $"{row.Name} failed to start and is now disabled: {msg}");
            }
            return new LaunchResult(false, row.Slug, BotStates.Failed, $"{row.Name} failed to start: {msg}. Next try in {after.Delay.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Why the agent image may not start, or null when it is the accepted one.</summary>
    public static string? ImageProblem(RosterConfig config, string? imageId)
    {
        static string Short(string id) => id.Length > 19 ? id[..19] : id;
        if (imageId is null) return $"The agent image {config.Bots.Image} is not on this host. Build or pull it, check it, then run oma-bot images accept.";
        if (config.Images.AcceptedDigest is null) return $"No agent image is accepted yet. Check {config.Bots.Image} ({Short(imageId)}), then run oma-bot images accept.";
        if (!string.Equals(config.Images.AcceptedDigest, imageId, StringComparison.Ordinal))
            return $"The agent image {config.Bots.Image} changed to {Short(imageId)} (accepted {Short(config.Images.AcceptedDigest)}). Check it, then run oma-bot images accept.";
        return null;
    }

    public async Task<LaunchResult> DownAsync(string name, CancellationToken ct = default)
    {
        var row = _bots.Find(name);
        if (row is null) return new LaunchResult(false, name, "missing", $"No bot named \"{name}\".");
        _bots.SetState(row.Slug, BotStates.Stopping);
        await supervisor.StopAsync("oma-bot-" + row.Slug, ct).ConfigureAwait(false);
        _bots.SetState(row.Slug, row.Enabled ? BotStates.Stopped : BotStates.Disabled);
        audit.Write("roster", "bot.down", row.Name, "");
        return new LaunchResult(true, row.Slug, BotStates.Stopped, $"{row.Name} stopped.");
    }

    /// <summary>Stops an idle bot's container and marks it sleeping. UpAsync wakes it.</summary>
    public async Task<LaunchResult> SleepAsync(string name, CancellationToken ct = default)
    {
        var row = _bots.Find(name);
        if (row is null) return new LaunchResult(false, name, "missing", $"No bot named \"{name}\".");
        await supervisor.StopAsync("oma-bot-" + row.Slug, ct).ConfigureAwait(false);
        _bots.SetState(row.Slug, BotStates.Sleeping);
        audit.Write("roster", "bot.sleep", row.Name, $"idle for {row.Spec.IdleSleepMinutes} minutes");
        return new LaunchResult(true, row.Slug, BotStates.Sleeping, $"{row.Name} is idle and now sleeping.");
    }

    /// <summary>The egress gate proxy URL for a bot (with its token), or null for no way out but the gateway.</summary>
    public Func<string, string?>? EgressProxyFor { get; set; }

    /// <summary>Where agent-host invites come from. Defaults to agent-host on this host.</summary>
    public HostExec.IHostInviteSource? HostInvites { get; set; }

    public static string InviteRecord(RosterPaths paths, string slug) => Path.Combine(paths.Bot(slug).Root, "host-invite.json");

    /// <summary>When the invite in a bot's container runs out (no secret is kept), or null.</summary>
    public static DateTimeOffset? InviteExpiry(RosterPaths paths, string slug)
    {
        var file = InviteRecord(paths, slug);
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("expires_at", out var e) && e.TryGetDateTimeOffset(out var t) ? t : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A fresh agent-host invite when the container will be created, none when
    /// the running container is kept. Without agent-host the bot still starts,
    /// with host commands off, and Spencer gets a notice.
    /// </summary>
    private async Task<HostExec.HostInvite?> HostInviteForAsync(BotRow row, CancellationToken ct)
    {
        var want = row.Spec.ContainerHash()[..16];
        var live = (await supervisor.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.TrimStart('/') == "oma-bot-" + row.Slug);
        if (live is { Running: true } && live.Labels.TryGetValue("oma-bot.spec", out var have) && have == want) return null;
        var source = HostInvites ??= new HostExec.HostBridgeClient(config, clock);
        var (invite, problem) = await source.MintAsync(row.Slug, _renderer.WorkspaceOf(row.Spec), ct).ConfigureAwait(false);
        if (invite is null)
        {
            notices.Add("warn", row.Name, $"{row.Name} starts without host commands: {problem}.", "host_invite:" + row.Slug);
            audit.Write("roster", "host_exec.invite_failed", row.Name, problem ?? "");
            return null;
        }
        SecureFile.WriteAllTextAtomic(InviteRecord(paths, row.Slug), $"{{\"expires_at\":\"{invite.ExpiresAt:O}\"}}\n");
        audit.Write("roster", "host_exec.invite", row.Name, $"invite valid until {invite.ExpiresAt:yyyy-MM-dd HH:mm} UTC");
        return invite;
    }

    private void Disable(BotRow row, string reason)
    {
        _bots.Disable(row.Slug, reason);
        audit.Write("roster", "bot.disabled", row.Name, reason);
        notices.Add("error", row.Name, $"{row.Name} was disabled: {reason}. Run oma-bot enable {row.Name} after fixing it.", "bot_disabled:" + row.Slug);
    }
}
