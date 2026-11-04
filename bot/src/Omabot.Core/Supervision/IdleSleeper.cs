// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Common;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Runtime;

namespace Omabot.Supervision;

/// <summary>
/// Idle sleep (plan 6.6): a running bot with no roster turn and no live work
/// for longer than its idle_sleep_minutes is stopped to free memory. Any wake
/// (a routine, an assignment, Spencer) starts it again; sessions survive as
/// files in the bot's data dir.
/// </summary>
public sealed class IdleSleeper(RosterDb db, IClock clock, BotLauncher launcher)
{
    private readonly BotStore _bots = new(db, clock);
    private readonly TurnContexts _turns = new(db, clock, Priority.Assignment);

    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var store = new AssignmentStore(db, clock, Redactor.Shared);
        foreach (var b in _bots.All().Where(b => b.Enabled && b.State == BotStates.Running))
        {
            // A host_exec bot sleeps before its agent-host invite runs out, so the next turn starts with a fresh one.
            var renew = b.Spec.HostExec && BotLauncher.InviteExpiry(launcher.Paths, b.Slug) is { } exp && exp - clock.UtcNow < TimeSpan.FromMinutes(15);
            if (!renew)
            {
                if (b.Spec.IdleSleepMinutes <= 0) continue;
                var last = DateTimeOffset.Parse(b.LastActiveAt ?? b.UpdatedAt, CultureInfo.InvariantCulture);
                if (clock.UtcNow - last < TimeSpan.FromMinutes(b.Spec.IdleSleepMinutes)) continue;
            }
            if (_turns.Busy(b.Slug)) continue;
            if (store.ForBot(b.Slug, AssignmentStatus.Queued, AssignmentStatus.Running, AssignmentStatus.Resume, AssignmentStatus.NeedsInput).Count > 0) continue;
            var r = await launcher.SleepAsync(b.Name, ct).ConfigureAwait(false);
            lines.Add(renew ? $"{b.Name}: sleeping to renew its host invite" : r.Message);
        }
        return lines;
    }
}
