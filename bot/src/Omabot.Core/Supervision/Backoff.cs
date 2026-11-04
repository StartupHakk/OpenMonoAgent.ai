// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Supervision;

/// <summary>
/// Restart backoff (plan 7.1): 5 s, 30 s, 2 min, 10 min. Four failures inside
/// 30 minutes disable the bot until a person runs `oma-bot enable`.
/// </summary>
public static class Backoff
{
    public static readonly TimeSpan[] Steps = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    // Plan 7.6: restart delays 5 s, 30 s, 2 min, 10 min; the next crash in the
    // window disables the bot (decision B19), so every step is used once.
    public const int DisableAfter = 5;

    public sealed record Decision(bool Disable, TimeSpan Delay, int FailuresInWindow);

    public static Decision Next(IEnumerable<DateTimeOffset> failures, DateTimeOffset now)
    {
        var recent = failures.Count(f => now - f <= Window && f <= now);
        if (recent >= DisableAfter) return new Decision(true, TimeSpan.Zero, recent);
        if (recent == 0) return new Decision(false, TimeSpan.Zero, 0);
        return new Decision(false, Steps[Math.Min(recent - 1, Steps.Length - 1)], recent);
    }
}

/// <summary>Bot lifecycle states (plan 6.6).</summary>
public static class BotStates
{
    public const string Defined = "defined";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Sleeping = "sleeping";
    public const string Stopping = "stopping";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Disabled = "disabled";

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [Defined] = [Starting, Disabled, Stopped],
        [Starting] = [Running, Failed, Stopping, Disabled],
        [Running] = [Sleeping, Stopping, Failed, Disabled, Starting],
        [Sleeping] = [Starting, Stopping, Disabled, Stopped],
        [Stopping] = [Stopped, Failed],
        [Stopped] = [Starting, Disabled],
        [Failed] = [Starting, Disabled, Stopped],
        [Disabled] = [Stopped],
    };

    public static bool CanMove(string from, string to) => Allowed.TryGetValue(from, out var next) && next.Contains(to);
}
