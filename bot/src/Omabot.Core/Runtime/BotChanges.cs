// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Team;

namespace Omabot.Runtime;

/// <summary>
/// Bot changes asked for by head-honcho (plan 7.16, idea rank 8). Creating
/// a bot, editing its instructions, personality, title or model, and
/// resuming a paused bot always become person-only cards; an edit carries
/// the version it was read at and applies only if the bot is still at that
/// version. Pausing applies at once because it can only make a bot quieter.
/// Every applied change is a bot_versions row naming who asked, so it can
/// be undone and so the requester can never approve for that bot later.
/// </summary>
public sealed class BotChanges(RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit, Notices notices, ApprovalStore approvals)
{
    public static readonly string[] EditableFields = ["title", "description", "seen_as", "personality", "instructions", "plan_prompt", "model"];
    private readonly BotStore _bots = new(db, clock);
    private readonly BotVersions _versions = new(db, clock);

    /// <summary>Writes the bot's files after an applied change (the service sets it to the renderer).</summary>
    public Action<BotSpec>? Provision { get; set; }

    public static string Version(BotSpec s) => s.Hash()[..12];

    public (ApprovalRow? Card, string? Problem) RequestUpdate(BotRow target, string field, string value, string version, string requesterKind, string requester, string origin)
    {
        if (Array.IndexOf(EditableFields, field) < 0) return (null, $"field must be one of {string.Join(", ", EditableFields)}.");
        if (!string.Equals(version, Version(target.Spec), StringComparison.Ordinal))
            return (null, $"conflict: {target.Name} changed since you read it; it is at version {Version(target.Spec)} now. Read list_bots again.");
        var after = target.Spec.Clone();
        if (Set(after, field, value) is { } problem) return (null, problem);
        if (field == "model" && !new TeamPlanner(db, config, paths, clock, audit).Live().KnownModels.Contains(value, StringComparer.OrdinalIgnoreCase))
            return (null, $"model \"{value}\" is not known to the gateway.");
        var diff = TeamPlanner.Diff(target.Spec, after);
        if (diff.Count == 0) return (null, "nothing would change.");
        var shown = value.Length > 1500 ? value[..1500] + " (cut)" : value;
        var card = approvals.Create(new ApprovalRequest(ApprovalKinds.InstructionChange, target.Slug,
            $"{requester} wants to change {target.Name}: {string.Join("; ", diff)}",
            new JsonObject { ["action"] = "update", ["bot"] = target.Slug, ["field"] = field, ["value"] = value, ["version"] = version },
            requesterKind, requester, origin, Ttl: TimeSpan.FromDays(7), PersonOnly: true, Action: $"{field}: {shown}"));
        audit.Write(requesterKind + ":" + requester, "bot.change_requested", target.Name, $"{field} card {card.Id}");
        return (card, null);
    }

    public (ApprovalRow? Card, string? Problem) RequestCreate(BotSpec spec, string requesterKind, string requester, string origin)
    {
        if (_bots.Find(spec.Name) is not null) return (null, $"a bot named {spec.Name} already exists.");
        var card = approvals.Create(new ApprovalRequest(ApprovalKinds.InstructionChange, spec.Slug,
            $"{requester} wants a new bot {spec.Name} ({spec.Title ?? "no title"}; {spec.PermissionProfile}; tools {string.Join(", ", spec.ToolsAllow)})",
            new JsonObject { ["action"] = "create", ["bot"] = spec.Slug, ["spec"] = spec.ToJson() },
            requesterKind, requester, origin, Ttl: TimeSpan.FromDays(7), PersonOnly: true, Action: "personality: " + (spec.Personality.Length > 1500 ? spec.Personality[..1500] + " (cut)" : spec.Personality)));
        audit.Write(requesterKind + ":" + requester, "bot.change_requested", spec.Name, $"create card {card.Id}");
        return (card, null);
    }

    public (ApprovalRow? Card, string? Problem) RequestResume(BotRow target, string requesterKind, string requester, string origin)
    {
        if (target.Enabled) return (null, $"{target.Name} is not paused.");
        var card = approvals.Create(new ApprovalRequest(ApprovalKinds.InstructionChange, target.Slug, $"{requester} wants to resume {target.Name} (paused: {target.DisabledReason})",
            new JsonObject { ["action"] = "resume", ["bot"] = target.Slug }, requesterKind, requester, origin, Ttl: TimeSpan.FromDays(7), PersonOnly: true, Action: "resume " + target.Name));
        audit.Write(requesterKind + ":" + requester, "bot.change_requested", target.Name, $"resume card {card.Id}");
        return (card, null);
    }

    public string Pause(BotRow target, string actor, string reason)
    {
        if (!target.Enabled) return $"{target.Name} is already paused.";
        _bots.Disable(target.Slug, $"paused by {actor}: {Redactor.Shared.Redact(reason)}");
        audit.Write(actor, "bot.pause", target.Name, reason);
        notices.Add("info", target.Name, $"{target.Name} was paused by {actor}: {reason}. Resume with oma-bot enable {target.Name}.", "bot_paused:" + target.Slug);
        return $"Paused {target.Name}. Spencer can resume it.";
    }

    /// <summary>Applies an allowed card once. A stale edit (the bot changed since it was read) is refused, not merged.</summary>
    public string Apply(ApprovalRow card)
    {
        if (card.Kind != ApprovalKinds.InstructionChange || card.DeliveredAt is not null) return "nothing to apply";
        if (!card.IsAllowed)
        {
            approvals.MarkDelivered(card.Id, "denied; nothing changed");
            return "denied; nothing changed";
        }
        var p = card.PayloadJson();
        var action = p["action"]?.GetValue<string>() ?? "";
        var requester = $"{card.RequesterKind}:{card.Requester}";
        var approver = $"{card.ApproverKind}:{card.Approver}";
        string result;
        switch (action)
        {
            case "update":
                {
                    var target = _bots.Find(card.Bot);
                    var field = p["field"]?.GetValue<string>() ?? "";
                    if (target is null) { result = "the bot no longer exists"; break; }
                    if (p["version"]?.GetValue<string>() != Version(target.Spec)) { result = $"{target.Name} changed since the card was made; nothing applied"; break; }
                    var after = target.Spec.Clone();
                    if (Set(after, field, p["value"]?.GetValue<string>() ?? "") is { } problem) { result = "not applied: " + problem; break; }
                    db.InTransaction((c, tx) =>
                    {
                        _bots.UpdateSpec(c, tx, target.Id, after);
                        audit.Write(c, tx, requester, "bot.change", target.Name, $"{field}, approved {card.Id} by {approver}");
                        _versions.Record(c, tx, after, requester, $"{field} (approved {card.Id} by {approver})");
                    });
                    Provision?.Invoke(after);
                    result = $"changed {target.Name}'s {field}; it takes effect when {target.Name} next starts";
                    break;
                }
            case "create":
                {
                    var spec = BotSpec.FromJson(p["spec"]?.GetValue<string>() ?? "{}");
                    if (_bots.Find(spec.Name) is not null) { result = $"a bot named {spec.Name} exists now; nothing created"; break; }
                    db.InTransaction((c, tx) =>
                    {
                        var port = _bots.NextPort(c, config.Bots.PortBase, config.Bots.PortCount);
                        _bots.Insert(c, tx, spec, port);
                        audit.Write(c, tx, requester, "bot.create", spec.Name, $"approved {card.Id} by {approver}, port {port}");
                        _versions.Record(c, tx, spec, requester, $"created (approved {card.Id} by {approver})");
                    });
                    Provision?.Invoke(spec);
                    result = $"created {spec.Name}";
                    break;
                }
            case "resume":
                {
                    var target = _bots.Find(card.Bot);
                    if (target is null) { result = "the bot no longer exists"; break; }
                    _bots.Enable(target.Slug);
                    audit.Write(approver, "bot.resume", target.Name, $"approved {card.Id}, requested by {requester}");
                    result = $"resumed {target.Name}";
                    break;
                }
            default:
                result = "unknown change; nothing applied";
                break;
        }
        approvals.MarkDelivered(card.Id, result);
        return result;
    }

    public IReadOnlyList<string> ApplyDecided()
        => [.. approvals.Undelivered().Where(a => a.Kind == ApprovalKinds.InstructionChange).Select(a => $"{a.Id}: {Apply(a)}")];

    /// <summary>
    /// Restores the spec a version replaced (oma-bot bot undo). Refused when
    /// the bot changed since that version, so a later change is never lost.
    /// </summary>
    public (bool Ok, string Message) Undo(string botName, long versionId, string actor)
    {
        var target = _bots.Find(botName);
        if (target is null) return (false, $"No bot named \"{botName}\".");
        var v = _versions.Get(versionId);
        if (v is null || v.Bot != target.Slug) return (false, $"{target.Name} has no version {versionId}.");
        if (v.SpecHash != target.Spec.Hash()) return (false, $"Refused: {target.Name} changed after version {versionId}; undo the later change first.");
        var before = _versions.Before(v);
        if (before is null) return (false, $"Version {versionId} created {target.Name}; there is nothing before it to restore.");
        var spec = before.Spec();
        if (!Names.SameName(spec.Name, target.Name)) return (false, "Refused: the earlier version has a different name; rename with the team file.");
        db.InTransaction((c, tx) =>
        {
            _bots.UpdateSpec(c, tx, target.Id, spec);
            audit.Write(c, tx, actor, "bot.undo", target.Name, $"restored version {before.Id} (undid {versionId})");
            _versions.Record(c, tx, spec, actor, $"undo of version {versionId}");
        });
        Provision?.Invoke(spec);
        return (true, $"Restored {target.Name} to version {before.Id}. It takes effect when {target.Name} next starts.");
    }

    private static string? Set(BotSpec s, string field, string value)
    {
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        switch (field)
        {
            case "title":
                if (value.Length > TeamParser.MaxTitleLength) return $"title is over {TeamParser.MaxTitleLength} characters.";
                s.Title = value.Length == 0 ? null : value;
                return null;
            case "description":
                if (value.Length > 2000) return "description is over 2,000 characters.";
                s.Description = value.Length == 0 ? null : value;
                return null;
            case "seen_as":
                if (value.Length > 300) return "seen_as is over 300 characters.";
                s.SeenAs = value.Length == 0 ? null : value;
                return null;
            case "personality":
                if (string.IsNullOrWhiteSpace(value)) return "personality cannot be empty.";
                if (System.Text.Encoding.UTF8.GetByteCount(value) > TeamParser.MaxPersonalityBytes) return $"personality is over {TeamParser.MaxPersonalityBytes} bytes.";
                s.Personality = value;
                return null;
            case "plan_prompt":
                if (System.Text.Encoding.UTF8.GetByteCount(value) > TeamParser.MaxPersonalityBytes) return $"plan_prompt is over {TeamParser.MaxPersonalityBytes} bytes.";
                s.PlanPrompt = value.Length == 0 ? null : value;
                return null;
            case "instructions":
                if (System.Text.Encoding.UTF8.GetByteCount(value) > TeamParser.MaxInstructionBytes) return $"instructions are over {TeamParser.MaxInstructionBytes} bytes.";
                if (value.Split('\n').Length > TeamParser.MaxInstructionLines) return $"instructions are over {TeamParser.MaxInstructionLines} lines.";
                s.Instructions = value.Length == 0 ? null : value;
                return null;
            case "model":
                if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,79}$")) return "model must be a model nickname.";
                s.Model = value;
                return null;
            default:
                return $"{field} cannot be changed this way.";
        }
    }
}
