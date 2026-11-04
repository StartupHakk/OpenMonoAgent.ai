// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Omabot.Common;

namespace Omabot.Routines;

/// <summary>
/// Quiet logging (plan 7.4): exactly one line per run in
/// logs/routines-DATE.log, with the start time in the routine's zone.
/// Lines pass through the redactor.
/// </summary>
public static class RoutineLog
{
    public static string Line(DateTimeOffset startUtc, string zone, string bot, string routine, string status, string? reason, int checks, int modelCalls, long tokens, long ms, bool notified)
    {
        var sb = new StringBuilder();
        sb.Append(RoutineRules.Local(startUtc, zone)).Append(' ').Append(bot).Append(' ').Append(routine).Append(' ').Append(status);
        if (!string.IsNullOrWhiteSpace(reason)) sb.Append(" reason=").Append(reason.Replace(' ', '_').Replace('\n', '_'));
        sb.Append(" checks=").Append(checks.ToString(CultureInfo.InvariantCulture));
        sb.Append(" model_calls=").Append(modelCalls.ToString(CultureInfo.InvariantCulture));
        if (tokens > 0) sb.Append(" tokens=").Append(tokens.ToString(CultureInfo.InvariantCulture));
        sb.Append(" ms=").Append(ms.ToString(CultureInfo.InvariantCulture));
        if (notified) sb.Append(" notified=yes");
        return Redactor.Shared.Redact(sb.ToString());
    }

    public static string FileFor(RosterPaths paths, DateTimeOffset startUtc, string zone)
    {
        var local = TimeZoneInfo.ConvertTime(startUtc, TimeZoneInfo.FindSystemTimeZoneById(zone));
        return Path.Combine(paths.Logs, $"routines-{local:yyyy-MM-dd}.log");
    }

    private static readonly Lock Gate = new();

    public static void Append(RosterPaths paths, DateTimeOffset startUtc, string zone, string line)
    {
        lock (Gate)
        {
            SecureFile.EnsureDirNoClobber(paths.Logs);
            var file = FileFor(paths, startUtc, zone);
            var existed = File.Exists(file);
            File.AppendAllText(file, line + "\n");
            if (!existed) SecureFile.TrySetMode(file);
        }
    }
}
