// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Omabot.Team;

namespace Omabot.Routines;

/// <summary>
/// When a routine fires (plan 7.4). cron uses <see cref="CronRules"/> with
/// the routine's own IANA zone; interval counts elapsed minutes from the last
/// fire; one_time fires once. Presets (weekdays, monthly, yearly) are turned
/// into cron when the team file is read, so only cron is stored.
/// </summary>
public static partial class RoutineRules
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 1440;
    /// <summary>The scan window after an outage. Older occurrences are summed in one missed row.</summary>
    public static readonly TimeSpan MaxScanWindow = TimeSpan.FromDays(30);

    public static readonly string[] OneTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];

    [GeneratedRegex("^([01]?[0-9]|2[0-3]):([0-5][0-9])$")]
    private static partial Regex HourMinute();

    [GeneratedRegex("^(last|[1-9]|[12][0-9]|3[01])\\s+(\\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MonthlyPattern();

    [GeneratedRegex("^(0?[1-9]|1[0-2])-(0?[1-9]|[12][0-9]|3[01])\\s+(\\S+)$")]
    private static partial Regex YearlyPattern();

    public static string Kind(RoutineSpec r)
        => r.Cron is not null ? "cron" : r.IntervalMinutes is not null ? "interval" : r.At is not null ? "one_time" : "watch";

    private static (int H, int M)? Time(string s)
    {
        var m = HourMinute().Match(s.Trim());
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>weekdays: "07:00" becomes "0 7 * * MON-FRI".</summary>
    public static string? WeekdaysCron(string value, out string? error)
    {
        error = null;
        if (Time(value) is not { } t) { error = "weekdays takes a time like 07:00."; return null; }
        return $"{t.M} {t.H} * * MON-FRI";
    }

    /// <summary>monthly: "1 09:00" or "last 09:00". Days that do not exist in a month are skipped; last handles every month.</summary>
    public static string? MonthlyCron(string value, out string? error)
    {
        error = null;
        var m = MonthlyPattern().Match(value.Trim());
        if (!m.Success || Time(m.Groups[2].Value) is not { } t) { error = "monthly takes a day (1 to 31 or last) and a time, like \"last 09:00\"."; return null; }
        var day = m.Groups[1].Value.Equals("last", StringComparison.OrdinalIgnoreCase) ? "L" : m.Groups[1].Value;
        return $"{t.M} {t.H} {day} * *";
    }

    /// <summary>yearly: "12-25 08:00" (month-day and time). February 29 runs only in leap years.</summary>
    public static string? YearlyCron(string value, out string? error)
    {
        error = null;
        var m = YearlyPattern().Match(value.Trim());
        if (!m.Success || Time(m.Groups[3].Value) is not { } t) { error = "yearly takes a month-day and a time, like \"12-25 08:00\"."; return null; }
        var month = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (day > DateTime.DaysInMonth(2024, month)) { error = "yearly names a day that never exists."; return null; }
        return $"{t.M} {t.H} {day} {month} *";
    }

    /// <summary>
    /// A one-time routine's instant. A local time inside a DST gap moves
    /// forward through the gap; a repeated local time means its first
    /// occurrence. Null when the text is not a date and time.
    /// </summary>
    public static DateTimeOffset? OneTimeUtc(string at, string zone)
    {
        if (!DateTime.TryParseExact(at.Trim(), OneTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var guard = 0;
        while (tz.IsInvalidTime(local) && guard++ < 24 * 60) local = local.AddMinutes(1);
        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// The next fire strictly after <paramref name="afterUtc"/>. Interval
    /// routines count whole steps from <paramref name="anchorUtc"/> (the last
    /// fire, or when the routine was first seen). Watch routines have none.
    /// </summary>
    public static DateTimeOffset? Next(RoutineSpec r, DateTimeOffset afterUtc, DateTimeOffset anchorUtc)
    {
        switch (Kind(r))
        {
            case "cron":
                return CronRules.Next(r.Cron!, r.Timezone, afterUtc);
            case "interval":
            {
                var step = TimeSpan.FromMinutes(r.IntervalMinutes!.Value);
                if (afterUtc < anchorUtc + step) return anchorUtc + step;
                var k = (long)Math.Floor((afterUtc - anchorUtc).Ticks / (double)step.Ticks) + 1;
                return anchorUtc + TimeSpan.FromTicks(step.Ticks * k);
            }
            case "one_time":
                return OneTimeUtc(r.At!, r.Timezone) is { } t && t > afterUtc ? t : null;
            default:
                return null;
        }
    }

    /// <summary>Occurrences in (fromUtc, toUtc], oldest first, lazily.</summary>
    public static IEnumerable<DateTimeOffset> Between(RoutineSpec r, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset anchorUtc)
    {
        if (toUtc <= fromUtc) yield break;
        switch (Kind(r))
        {
            case "cron":
                foreach (var t in CronRules.Between(r.Cron!, r.Timezone, fromUtc, toUtc)) yield return t;
                break;
            case "interval":
            {
                var t = Next(r, fromUtc, anchorUtc);
                var step = TimeSpan.FromMinutes(r.IntervalMinutes!.Value);
                while (t is { } v && v <= toUtc)
                {
                    yield return v;
                    t = v + step;
                }
                break;
            }
            case "one_time":
                if (OneTimeUtc(r.At!, r.Timezone) is { } once && once > fromUtc && once <= toUtc) yield return once;
                break;
        }
    }

    /// <summary>The next three fire times, for cards and `routine_next_runs`.</summary>
    public static IReadOnlyList<DateTimeOffset> NextThree(RoutineSpec r, DateTimeOffset nowUtc, DateTimeOffset anchorUtc)
    {
        var list = new List<DateTimeOffset>();
        var after = nowUtc;
        for (var i = 0; i < 3; i++)
        {
            var n = Next(r, after, anchorUtc);
            if (n is null) break;
            list.Add(n.Value);
            after = n.Value;
        }
        return list;
    }

    public static string Local(DateTimeOffset utc, string zone)
        => TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string LocalShort(DateTimeOffset utc, string zone)
        => TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(zone)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Plain-language problems with a routine rule, or an empty list. Shared by team apply and the routine tools.</summary>
    public static List<string> Problems(RoutineSpec r, int minInterval, DateTimeOffset nowUtc, bool newOrReenabled)
    {
        var p = new List<string>();
        if (!TeamResolver.IsZone(r.Timezone)) p.Add($"\"{r.Timezone}\" is not an IANA time zone.");
        var triggers = new[] { r.Cron is not null, r.IntervalMinutes is not null, r.At is not null, r.Watch is not null }.Count(x => x);
        if (triggers != 1) p.Add("Give exactly one trigger: cron, interval_minutes, at, watch, weekdays, monthly or yearly.");
        if (r.IntervalMinutes is { } iv && (iv < Math.Max(MinIntervalMinutes, minInterval) || iv > MaxIntervalMinutes))
            p.Add($"interval_minutes must be {Math.Max(MinIntervalMinutes, minInterval)} to {MaxIntervalMinutes}.");
        if (r.Cron is { } cron && CronRules.Validate(cron) is { } err) p.Add(err);
        if (r.At is { } at && TeamResolver.IsZone(r.Timezone))
        {
            if (OneTimeUtc(at, r.Timezone) is not { } when) p.Add("at must be a local date and time like 2026-11-04 07:00.");
            else if (newOrReenabled && r.Enabled && when <= nowUtc) p.Add("at is in the past; a one-time routine cannot be turned on with a past time.");
        }
        if (r.Watch is { } w && !WatchTarget.TryParse(w, out _, out var werr)) p.Add(werr!);
        if (r.Notify is not ("errors_only" or "never")) p.Add("notify must be errors_only or never (hard rule 8: routines notify only on errors).");
        if (r.Session is not ("fresh" or "standing")) p.Add("session must be fresh or standing.");
        if (r.HostExec is not ("off" or "ask")) p.Add("host_exec must be off or ask.");
        if (r.DailyTokenBudget < 0) p.Add("daily_token_budget cannot be negative.");
        if (r.Instructions.Length > 12000) p.Add("instructions are over 12,000 characters.");
        foreach (var (c, i) in r.Checks.Select((c, i) => (c, i)))
            if (Checks.Problem(c) is { } cp) p.Add($"checks[{i}] {c.Kind}: {cp}");
        return p;
    }
}

/// <summary>What a watch trigger fingerprints: a workspace path, the bot's inbox, a check script, or a read-only roster result.</summary>
public sealed record WatchTarget(string Kind, string Value)
{
    public static bool TryParse(string text, out WatchTarget target, out string? error)
    {
        error = null;
        target = new WatchTarget("path", text);
        var t = text.Trim();
        if (t == "assignments") { target = new("assignments", ""); return true; }
        if (t == "doctor") { target = new("doctor", ""); return true; }
        if (t.StartsWith("command:", StringComparison.Ordinal))
        {
            var script = t[8..].Trim();
            if (!Checks.IsScriptName(script.Split(' ', 2)[0])) { error = "watch command: names a script in bot/checks (letters, digits, dot, dash, underscore)."; return false; }
            target = new("command", script);
            return true;
        }
        if (!TeamParser.SafeRelativeOrAbsolute(t) || Path.IsPathRooted(t)) { error = "watch is a workspace path (no ~, .., . or leading /), assignments, doctor or command:SCRIPT."; return false; }
        target = new("path", t);
        return true;
    }
}
