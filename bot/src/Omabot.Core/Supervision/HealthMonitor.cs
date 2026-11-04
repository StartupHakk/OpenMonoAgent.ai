// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Acp;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Supervision;

/// <summary>
/// Health probe (plan 7.1): every tick, each running bot must answer
/// discovery. A bot that does not is restarted after its backoff delay;
/// four failures in 30 minutes disable it.
/// </summary>
public sealed class HealthMonitor(RosterDb db, IClock clock, BotLauncher launcher, Func<int, AcpClient> acpFor)
{
    private readonly BotStore _bots = new(db, clock);

    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct = default)
    {
        var actions = new List<string>();
        foreach (var b in _bots.All().Where(b => b.Enabled && b.State is BotStates.Running or BotStates.Failed or BotStates.Starting))
        {
            if (b.State == BotStates.Running)
            {
                var disc = await acpFor(b.Port).DiscoveryAsync(ct).ConfigureAwait(false);
                if (disc is { Status: "ready" }) continue;
                _bots.RecordFailure(b.Id, "health probe: no discovery answer");
                _bots.SetState(b.Slug, BotStates.Failed, "health probe: no discovery answer");
                actions.Add($"{b.Name}: health probe failed");
            }
            var failures = db.Query("SELECT at FROM bot_failures WHERE bot_id=$b ORDER BY at DESC LIMIT 10;",
                r => DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), ("$b", b.Id));
            var decision = Backoff.Next(failures, clock.UtcNow);
            var last = failures.Count > 0 ? failures[0] : DateTimeOffset.MinValue;
            if (!decision.Disable && clock.UtcNow - last < decision.Delay)
            {
                actions.Add($"{b.Name}: waiting {Math.Ceiling((decision.Delay - (clock.UtcNow - last)).TotalSeconds)} s before restart");
                continue;
            }
            var result = await launcher.UpAsync(b.Name, ct).ConfigureAwait(false);
            actions.Add($"{b.Name}: {result.Message}");
        }
        return actions;
    }
}
