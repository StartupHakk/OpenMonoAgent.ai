// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Runtime;

/// <summary>
/// Where handed-out work goes (plan 7.5 "Routing"): an explicit bot name,
/// "lead" for the requester's team lead (or head-honcho), or "auto" for the
/// team file's keyword routes, falling back to the lead. No model is used.
/// </summary>
public static class Routing
{
    public sealed record Route(BotRow? Bot, string? Problem, string How);

    public static Route Resolve(string to, string brief, BotRow? requester, RosterDb db, BotStore bots)
    {
        var t = (to ?? "").Trim();
        if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var words = Memory.Recall.Words(brief).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var json in db.Query("SELECT json FROM team_items WHERE kind='route' ORDER BY name;", r => r.GetString(0)))
            {
                using var doc = JsonDocument.Parse(json);
                var routeWords = doc.RootElement.GetProperty("words").EnumerateArray().Select(w => w.GetString() ?? "");
                if (!routeWords.Any(w => words.Contains(w))) continue;
                var target = bots.Find(doc.RootElement.GetProperty("to").GetString() ?? "");
                if (target is not null && (requester is null || target.Slug != requester.Slug)) return new Route(target, null, "keyword route");
            }
            return Lead(requester, db, bots) is { } lead ? new Route(lead, null, "lead (no keyword route matched)") : new Route(null, "No keyword route matched and there is no lead bot.", "auto");
        }
        if (t.Equals("lead", StringComparison.OrdinalIgnoreCase))
        {
            var lead = Lead(requester, db, bots);
            if (lead is null) return new Route(null, "There is no lead bot for this team.", "lead");
            if (requester is not null && lead.Slug == requester.Slug) return new Route(null, "You are the lead; name the bot to hand this to.", "lead");
            return new Route(lead, null, "team lead");
        }
        var bot = bots.Find(t);
        return bot is null ? new Route(null, $"No bot named \"{t}\".", "name") : new Route(bot, null, "name");
    }

    /// <summary>The requester's team lead from the team file, else the roster lead bot (role lead, head-honcho).</summary>
    public static BotRow? Lead(BotRow? requester, RosterDb db, BotStore bots)
    {
        if (requester?.Spec.Team is { } team)
        {
            foreach (var json in db.Query("SELECT json FROM team_items WHERE kind='team';", r => r.GetString(0)))
            {
                using var doc = JsonDocument.Parse(json);
                if (Names.SameName(doc.RootElement.GetProperty("name").GetString() ?? "", team)
                    && doc.RootElement.GetProperty("lead").GetString() is { } leadName && bots.Find(leadName) is { } lead)
                    return lead;
            }
        }
        return bots.All().FirstOrDefault(b => b.Spec.Role == "lead");
    }
}
