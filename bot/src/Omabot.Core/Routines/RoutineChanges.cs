// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Team;

namespace Omabot.Routines;

/// <summary>
/// Routine changes asked for by a bot (plan 7.4 "Bot tools for routines").
/// A bot never changes its routines directly: create, update, delete and
/// resume become approval cards that show the rule, the zone and the next
/// three times, and apply only after a person allows them. Pausing applies
/// at once because it can only make a bot quieter.
/// </summary>
public sealed class RoutineChanges(RosterDb db, RosterConfig config, IClock clock, AuditLog audit, ApprovalStore approvals)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Validates and files a card. Returns the card id or the problems.</summary>
    public (ApprovalRow? Card, IReadOnlyList<string> Problems) Request(string botSlug, string action, RoutineSpec? spec, string name, string requesterKind, string requester, string origin = "person")
    {
        var bots = new BotStore(db, clock);
        var row = bots.Find(botSlug) ?? throw new KeyNotFoundException($"No bot {botSlug}.");
        var problems = new List<string>();
        var existing = row.Spec.Routines.FirstOrDefault(r => Names.SameName(r.Name, name));
        switch (action)
        {
            case "create":
                if (existing is not null) problems.Add($"{row.Name} already has a routine named {name}.");
                if (row.Spec.Routines.Count >= config.Routines.MaxPerBot) problems.Add($"A bot may have at most {config.Routines.MaxPerBot} routines.");
                break;
            case "update" or "delete" or "resume":
                if (existing is null) problems.Add($"{row.Name} has no routine named {name}.");
                break;
            default:
                problems.Add("action must be create, update, delete or resume.");
                break;
        }
        if (spec is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(spec.Name, "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,59}$")) problems.Add("routine names use letters, digits, spaces, dot, dash and underscore, up to 60 characters.");
            problems.AddRange(RoutineRules.Problems(spec, config.Routines.MinIntervalMinutes, clock.UtcNow, newOrReenabled: true));
            foreach (var c in spec.Checks.Where(c => c.Kind == "http_status"))
                if (Uri.TryCreate(c.Args.GetValueOrDefault("url"), UriKind.Absolute, out var u) && !Egress.AddressPolicy.HostAllowed(u.Host, row.Spec.AllowHosts))
                    problems.Add($"http_status host {u.Host} is not on {row.Name}'s web.allow_hosts.");
        }
        if (problems.Count > 0) return (null, problems);
        var shown = spec ?? existing!;
        var next = RoutineRules.NextThree(shown, clock.UtcNow, clock.UtcNow).Select(t => RoutineRules.LocalShort(t, shown.Timezone)).ToList();
        var verb = action switch { "create" => "wants a new routine", "update" => "wants to change routine", "delete" => "wants to delete routine", _ => "wants to resume routine" };
        var summary = $"{row.Name} {verb} \"{name}\": {shown.Describe()}{(next.Count > 0 ? ", next " + string.Join(", ", next) : "")}";
        var payload = new JsonObject
        {
            ["action"] = action,
            ["bot"] = row.Slug,
            ["routine"] = name,
            ["spec"] = spec is null ? null : JsonSerializer.SerializeToNode(spec, Json),
            ["next"] = new JsonArray([.. next.Select(n => (JsonNode)n)]),
        };
        var card = approvals.Create(new ApprovalRequest(ApprovalKinds.RoutineChange, row.Slug, summary, payload, requesterKind, requester, origin, Ttl: TimeSpan.FromDays(7)));
        audit.Write(requesterKind + ":" + requester, "routine.change_requested", $"{row.Name}/{name}", $"{action} card {card.Id}");
        return (card, []);
    }

    /// <summary>Applies an allowed routine change card. Idempotent: a delivered card is never applied twice.</summary>
    public string Apply(ApprovalRow card)
    {
        if (card.Kind != ApprovalKinds.RoutineChange || card.DeliveredAt is not null) return "nothing to apply";
        if (!card.IsAllowed)
        {
            approvals.MarkDelivered(card.Id, "denied; nothing changed");
            return "denied; nothing changed";
        }
        var p = card.PayloadJson();
        var action = p["action"]?.GetValue<string>() ?? "";
        var name = p["routine"]?.GetValue<string>() ?? "";
        var spec = p["spec"] is JsonObject so ? so.Deserialize<RoutineSpec>(Json) : null;
        var bots = new BotStore(db, clock);
        var row = bots.Find(card.Bot);
        if (row is null)
        {
            approvals.MarkDelivered(card.Id, "the bot no longer exists");
            return "the bot no longer exists";
        }
        var botSpec = row.Spec.Clone();
        var idx = botSpec.Routines.FindIndex(r => Names.SameName(r.Name, name));
        string result;
        switch (action)
        {
            case "create" when spec is not null && idx < 0:
                botSpec.Routines.Add(spec);
                result = $"created routine {name}";
                break;
            case "update" when spec is not null && idx >= 0:
                botSpec.Routines[idx] = spec;
                result = $"updated routine {name}";
                break;
            case "delete" when idx >= 0:
                botSpec.Routines.RemoveAt(idx);
                result = $"deleted routine {name}";
                break;
            case "resume":
                var store = new RoutineStore(db, clock);
                if (store.Find(row.Slug, name) is { } st) store.SetPaused(st.Id, false);
                result = $"resumed routine {name}";
                break;
            default:
                approvals.MarkDelivered(card.Id, "the routine changed since the card was made; nothing applied");
                return "the routine changed since the card was made; nothing applied";
        }
        if (action != "resume")
        {
            db.InTransaction((c, tx) =>
            {
                bots.UpdateSpec(c, tx, row.Id, botSpec);
                audit.Write(c, tx, $"{card.ApproverKind}:{card.Approver}", "routine." + action, $"{row.Name}/{name}", $"approved {card.Id}, requested by {card.RequesterKind}:{card.Requester}");
            });
        }
        else audit.Write($"{card.ApproverKind}:{card.Approver}", "routine.resume", $"{row.Name}/{name}", $"approved {card.Id}");
        approvals.MarkDelivered(card.Id, result);
        return result;
    }

    /// <summary>Applies every decided routine card not yet applied (the service tick and the CLI after an answer).</summary>
    public IReadOnlyList<string> ApplyDecided()
        => [.. approvals.Undelivered().Where(a => a.Kind == ApprovalKinds.RoutineChange).Select(a => $"{a.Id}: {Apply(a)}")];
}
