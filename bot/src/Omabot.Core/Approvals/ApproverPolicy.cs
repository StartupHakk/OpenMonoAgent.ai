// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Runtime;

namespace Omabot.Approvals;

/// <summary>
/// The approver list: bots Spencer named in roster.json (approvals.approver_bots)
/// or in the team file (approvers), each for some card kinds. Only a person's
/// edit of either file changes it; no tool writes it.
/// </summary>
public static class ApproverList
{
    public static IReadOnlyList<(string Bot, IReadOnlyList<string> Kinds)> Read(RosterDb db, RosterConfig config)
    {
        var list = config.Approvals.ApproverBots.Select(a => (a.Bot, (IReadOnlyList<string>)a.Kinds)).ToList();
        var json = db.Query("SELECT json FROM team_items WHERE kind='approvers' AND name='all';", r => r.GetString(0)).FirstOrDefault();
        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var a in doc.RootElement.EnumerateArray())
                list.Add((a.GetProperty("bot").GetString() ?? "", a.GetProperty("kinds").EnumerateArray().Select(k => k.GetString() ?? "").ToList()));
        }
        return list;
    }

    public static bool Allows(RosterDb db, RosterConfig config, string bot, string kind)
        => Array.IndexOf(ApprovalSettings.BotApprovableKinds, kind) >= 0
           && Read(db, config).Any(a => Names.SameName(a.Bot, bot) && a.Kinds.Contains(kind, StringComparer.Ordinal));

    /// <summary>Listed approver bots for a kind, in list order.</summary>
    public static IReadOnlyList<string> For(RosterDb db, RosterConfig config, string kind)
        => Array.IndexOf(ApprovalSettings.BotApprovableKinds, kind) < 0
            ? []
            : [.. Read(db, config).Where(a => a.Kinds.Contains(kind, StringComparer.Ordinal)).Select(a => a.Bot).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// No self-approval (plan 7.6, hard rule 18). A person may answer when their
/// role approves and the card is theirs. A bot may answer only a card the
/// broker bound to it, of a kind Spencer listed it for, and never when it is
/// the requester, an ancestor or descendant of the requester's work, a bot
/// the requester controls or manages, a bot the requester created or edited,
/// or head-honcho or the health bot. Identities come from the roster's own
/// records (CLI user, web login, bot socket), never from a request body.
/// </summary>
public sealed class ApproverPolicy(RosterDb db, IClock clock, RosterConfig config) : IApproverPolicy
{
    private readonly BotStore _bots = new(db, clock);
    private readonly AssignmentStore _assignments = new(db, clock, Redactor.Shared);
    private readonly BotVersions _versions = new(db, clock);

    public string? Refusal(ApprovalRow row, Approver approver)
    {
        if (approver.Kind == "person") return PersonRefusal(row, approver);
        if (approver.Kind != "bot") return "unknown approver.";
        return BotRefusal(row, approver.Id);
    }

    private static string? PersonRefusal(ApprovalRow row, Approver p)
    {
        if (p.Role is not ("owner" or "operator")) return "this account may not answer approvals.";
        if (p.Id.Length == 0) return "the answer has no person identity.";
        if (row.UserId is not null && !string.Equals(row.UserId, p.UserId, StringComparison.Ordinal)) return "this card belongs to another user.";
        if (row.BoundTo is { } b && b.StartsWith("person:", StringComparison.Ordinal) && !string.Equals(b, p.ToString(), StringComparison.Ordinal))
            return "this card is bound to another person.";
        return null;
    }

    private string? BotRefusal(ApprovalRow row, string botId)
    {
        if (Names.SameName(botId, row.Bot) || Names.SameName(botId, row.Requester)) return "a bot never approves its own request.";
        if (row.PersonOnly || Array.IndexOf(ApprovalSettings.BotApprovableKinds, row.Kind) < 0) return "only a person may answer this card.";
        if (row.BoundTo is null || !string.Equals(row.BoundTo, "bot:" + Names.Slug(botId), StringComparison.Ordinal)) return "this card is not bound to you.";
        var me = _bots.Find(botId);
        if (me is null || !me.Enabled) return "the approver bot is missing or disabled.";
        if (me.Spec.Role is "lead" or "health") return "head-honcho and the health bot hold no approval power.";
        if (!ApproverList.Allows(db, config, me.Name, row.Kind)) return $"{me.Name} is not listed as an approver for {row.Kind} cards.";
        return ControlRefusal(row, me);
    }

    /// <summary>The control chain rules, shared with the broker when it picks an approver to bind.</summary>
    public string? ControlRefusal(ApprovalRow row, BotRow me)
    {
        var requester = _bots.Find(row.Bot);
        if (requester is not null)
        {
            if (requester.Spec.Manages.Any(m => Names.SameName(m, me.Name))) return "the requester manages the approver.";
            if (_versions.ChangedBy(me.Slug, me.Name, "bot:" + requester.Slug)) return "the requester created or edited the approver.";
        }
        if (row.AssignmentId is { } aid && _assignments.Get(aid) is { } work)
        {
            for (var a = work.ParentId is null ? null : _assignments.Get(work.ParentId); a is not null; a = a.ParentId is null ? null : _assignments.Get(a.ParentId))
                if (Names.SameName(a.AssigneeBot, me.Slug)) return "the approver is an ancestor of the requester's work.";
            if (work.RequesterBot is { } rb && Names.SameName(rb, me.Slug)) return "the approver handed this work to the requester.";
            var stack = new Stack<string>([aid]);
            while (stack.Count > 0)
            {
                foreach (var child in _assignments.Children(stack.Pop()))
                {
                    if (Names.SameName(child.AssigneeBot, me.Slug)) return "the approver works for the requester (a descendant of its work).";
                    stack.Push(child.Id);
                }
            }
        }
        if (requester is not null && _assignments.ByStatus(AssignmentStatus.Live).Any(x => x.RequesterBot is { } r && Names.SameName(r, requester.Slug) && Names.SameName(x.AssigneeBot, me.Slug)))
            return "the requester controls the approver right now (it handed the approver work).";
        return null;
    }
}
