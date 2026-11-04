// SPDX-License-Identifier: AGPL-3.0-or-later
using Cronos;

namespace Omabot.Routines;

/// <summary>
/// Cron parsing with a required IANA zone (builder choice B17: Cronos).
/// Five fields, with L, W and # supported. DST: a time in the spring gap moves
/// forward to the first valid time; a repeated autumn hour runs once.
/// </summary>
public static class CronRules
{
    public static string? Validate(string expression)
    {
        try
        {
            _ = Parse(expression);
            return null;
        }
        catch (CronFormatException ex)
        {
            return "Not a valid cron expression: " + ex.Message;
        }
    }

    public static CronExpression Parse(string expression)
    {
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) throw new CronFormatException("Use five fields: minute hour day month weekday.");
        return CronExpression.Parse(string.Join(' ', fields), CronFormat.Standard);
    }

    /// <summary>Next fire strictly after <paramref name="afterUtc"/>, in UTC.</summary>
    public static DateTimeOffset? Next(string expression, string zone, DateTimeOffset afterUtc)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var next = Parse(expression).GetNextOccurrence(afterUtc.UtcDateTime, tz, inclusive: false);
        return next is null ? null : new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc));
    }

    /// <summary>All fires in (fromUtc, toUtc], in UTC.</summary>
    public static IEnumerable<DateTimeOffset> Between(string expression, string zone, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        foreach (var t in Parse(expression).GetOccurrences(fromUtc.UtcDateTime, toUtc.UtcDateTime, tz, fromInclusive: false, toInclusive: true))
            yield return new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc));
    }
}
