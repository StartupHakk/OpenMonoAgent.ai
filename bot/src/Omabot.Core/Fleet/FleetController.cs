// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Fleet;

public sealed class FleetException(string message) : Exception(message);

public sealed record FleetNode(string Name, int Slot, IReadOnlyDictionary<string, string> Labels, bool NeverTouch, string State, string? Fingerprint, string? Version, int Capacity, string? LastHeartbeat);

public sealed record FleetJob(string Id, string Key, string Kind, JsonObject Spec, string SpecHash, string State, string? ApprovalId, string? ApprovedBy,
    string RequestedBy, string? Node, string? LeaseId, DateTimeOffset? LeaseExpiresAt, long Fencing, int Attempts, string? Reason);

/// <summary>A job handed to one node: valid until <see cref="ExpiresAt"/> and only with this fencing token.</summary>
public sealed record FleetLease(string JobId, string LeaseId, long Fencing, DateTimeOffset ExpiresAt, JsonObject Spec);

/// <summary>What a node API call came to: 200 on success, 409 for a stale lease or a step that must not run again, 4xx otherwise.</summary>
public sealed record FleetReply(bool Ok, int Status, string Message, JsonObject? Data = null);

/// <summary>
/// The fleet controller (plan 7.21, phase 12). Off unless Spencer turned it on
/// (placement.mode fleet and fleet.enabled_by). Nodes enroll with a one-time
/// code bound to their name and a key that never leaves them, and work only
/// after Spencer approves the key's fingerprint. Jobs are made by a person on
/// the CLI; anything but a read-only kind waits for a person's card bound to
/// the job hash, and approved_by is written here from the approval record,
/// never taken from a job or a node. Nodes pull work under leases with
/// fencing tokens; outward steps keep an intent and done ledger, and a job
/// whose node vanished between the two is never handed out again: a person
/// checks the outside system first.
/// </summary>
public sealed partial class FleetController(RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit, Notices notices, FleetCa ca)
{
    public static readonly string[] ReadOnlyKinds = ["readonly_query", "routine_check"];
    public static readonly string[] Kinds = ["readonly_query", "routine_check", "run_playbook", "deploy", "render", "backfill"];
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(15);
    public const int MaxResultBytes = 64 * 1024;
    /// <summary>A node's gateway token lives beside the bots' tokens under this reserved name, so the gateway serves it like a bot and nothing else.</summary>
    public const string NodeTokenPrefix = "fleetnode-";
    public const long MaxArtifactBytes = 64L << 20;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex NodeName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$")]
    private static partial Regex Key();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex Item();

    private string Now => Iso(clock.UtcNow);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public FleetCa Ca => ca;

    /// <summary>Why fleet work is refused, or null. Fleet mode is on only by a person's explicit choice (decision B14).</summary>
    public string? GuardProblem()
        => config.Placement.Mode != "fleet" ? "Fleet mode is off. It starts only on Spencer's direction: oma-bot fleet enable --by NAME --confirm."
        : string.IsNullOrWhiteSpace(config.Fleet.EnabledBy) ? "fleet.enabled_by must name the person who turned fleet mode on."
        : null;

    private void Guard()
    {
        if (GuardProblem() is { } p) throw new FleetException(p);
    }

    // ---------- nodes ----------

    public IReadOnlyList<FleetNode> Nodes() => db.Query("SELECT name, slot, labels_json, never_touch, state, fingerprint, version, capacity, last_heartbeat FROM fleet_nodes ORDER BY slot;", MapNode);

    public FleetNode? Node(string name) => db.Query("SELECT name, slot, labels_json, never_touch, state, fingerprint, version, capacity, last_heartbeat FROM fleet_nodes WHERE name=$n;", MapNode, ("$n", name)).FirstOrDefault();

    private static FleetNode MapNode(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetInt32(1),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [], r.GetInt64(3) != 0, r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8));

    /// <summary>A one-time enrollment code for one named node, valid for 15 minutes.</summary>
    public string Invite(string node, IReadOnlyDictionary<string, string> labels, bool neverTouch, string by)
    {
        Guard();
        if (!NodeName().IsMatch(node)) throw new FleetException("Node names are lower case letters, digits and dashes, starting with a letter.");
        foreach (var (k, v) in labels)
            if (!Item().IsMatch(k) || !Item().IsMatch(v)) throw new FleetException($"Label {k}={v} must be lower case letters, digits, dots, dashes or underscores.");
        var existing = Node(node);
        if (existing is { State: not "revoked" }) throw new FleetException($"Node {node} is already {existing.State}. Revoke it first to enroll it again.");
        var slot = existing?.Slot ?? (Enumerable.Range(1, config.Fleet.MaxNodes).Cast<int?>().FirstOrDefault(s => !Nodes().Any(n => n.Slot == s))
            ?? throw new FleetException($"All {config.Fleet.MaxNodes} node slots are in use."));
        var labelsJson = JsonSerializer.Serialize(labels.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary());
        if (existing is null)
            db.Exec("INSERT INTO fleet_nodes(name, slot, labels_json, never_touch, state, created_by, created_at, updated_at) VALUES ($n,$s,$l,$nt,'invited',$b,$t,$t);",
                ("$n", node), ("$s", slot), ("$l", labelsJson), ("$nt", neverTouch ? 1 : 0), ("$b", by), ("$t", Now));
        else
            db.Exec("UPDATE fleet_nodes SET labels_json=$l, never_touch=$nt, state='invited', fingerprint=NULL, token_hash=NULL, updated_at=$t WHERE name=$n;",
                ("$n", node), ("$l", labelsJson), ("$nt", neverTouch ? 1 : 0), ("$t", Now));
        var code = "fnc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        db.Exec("INSERT INTO fleet_invites(code_hash, node, expires_at, created_by) VALUES ($h,$n,$e,$b);",
            ("$h", SecureFile.Sha256Hex(code)), ("$n", node), ("$e", Iso(clock.UtcNow + InviteLifetime)), ("$b", by));
        Redactor.Shared.AddKnownSecret(code);
        audit.Write(by, "fleet.invite", node, $"slot {slot}, labels {labelsJson}{(neverTouch ? ", never touch" : "")}");
        return code;
    }

    /// <summary>Spends an invite once and signs the node's own key. The node works only after a person approves the fingerprint.</summary>
    public FleetReply Enroll(string? code, string? node, string? csrPem)
    {
        if (GuardProblem() is { } g) return new(false, 403, g);
        if (code is null || node is null || csrPem is null || !NodeName().IsMatch(node)) return new(false, 400, "code, node and csr are required.");
        var hash = SecureFile.Sha256Hex(code);
        var invite = db.Query("SELECT node, expires_at, used_at FROM fleet_invites WHERE code_hash=$h;", r => (Node: r.GetString(0), Expires: Parse(r.GetString(1)), Used: !r.IsDBNull(2)), ("$h", hash)).FirstOrDefault();
        string? why = invite.Node is null ? "unknown code" : invite.Used ? "code already used" : invite.Expires <= clock.UtcNow ? "code expired" : invite.Node != node ? "code is for another node" : null;
        if (why is null && db.Exec("UPDATE fleet_invites SET used_at=$t WHERE code_hash=$h AND used_at IS NULL;", ("$t", Now), ("$h", hash)) != 1) why = "code already used";
        if (why is not null)
        {
            audit.Write("fleet", "fleet.enroll_refused", node, why);
            return new(false, 403, "Enrollment refused.");
        }
        X509Certificate2 cert;
        try { cert = ca.SignNode(node, csrPem); }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException)
        {
            audit.Write("fleet", "fleet.enroll_refused", node, "bad certificate request");
            return new(false, 400, "The certificate request is not valid for this node.");
        }
        var fingerprint = FleetCa.Fingerprint(cert);
        db.Exec("UPDATE fleet_nodes SET state='pending', fingerprint=$f, updated_at=$t WHERE name=$n;", ("$f", fingerprint), ("$t", Now), ("$n", node));
        audit.Write("fleet", "fleet.enrolled", node, $"key fingerprint {fingerprint}; waiting for a person to approve it");
        notices.Add("warn", null, $"Fleet node {node} enrolled with key fingerprint {fingerprint[..16]}. Check it on the node, then run: oma-bot fleet approve {node} --fingerprint {fingerprint}", "fleet_enroll:" + node + ":" + fingerprint[..12]);
        return new(true, 200, "Enrolled; waiting for approval.", new JsonObject { ["certificate"] = cert.ExportCertificatePem(), ["ca"] = ca.CaPem(), ["fingerprint"] = fingerprint, ["slot"] = Node(node)!.Slot });
    }

    /// <summary>A person confirms the fingerprint the node showed; only then may it lease work.</summary>
    public void Approve(string node, string fingerprint, string by)
    {
        Guard();
        var n = Node(node) ?? throw new FleetException($"No node {node}.");
        if (n.State != "pending") throw new FleetException($"Node {node} is {n.State}, not waiting for approval.");
        if (!SecureFile.FixedTimeEquals(n.Fingerprint, fingerprint.Trim().ToLowerInvariant())) throw new FleetException("That fingerprint does not match the key the node enrolled with.");
        var token = new Secrets.BotTokens(paths, Redactor.Shared).Rotate(NodeTokenPrefix + node);
        db.Exec("UPDATE fleet_nodes SET state='enrolled', token_hash=$h, updated_at=$t WHERE name=$n;", ("$h", SecureFile.Sha256Hex(token)), ("$t", Now), ("$n", node));
        audit.Write(by, "fleet.approve", node, "fingerprint " + fingerprint + "; gateway token issued");
    }

    public void Drain(string node, string by)
    {
        if (db.Exec("UPDATE fleet_nodes SET state='draining', updated_at=$t WHERE name=$n AND state IN ('enrolled','draining');", ("$t", Now), ("$n", node)) != 1) throw new FleetException($"Node {node} is not enrolled.");
        audit.Write(by, "fleet.drain", node, "no new leases");
    }

    /// <summary>Revokes at once: its certificate is refused, its leases end, and it must enroll again with a new invite.</summary>
    public void Revoke(string node, string by)
    {
        if (db.Exec("UPDATE fleet_nodes SET state='revoked', token_hash=NULL, updated_at=$t WHERE name=$n;", ("$t", Now), ("$n", node)) != 1) throw new FleetException($"No node {node}.");
        File.Delete(paths.Bot(NodeTokenPrefix + node).TokenFile);
        foreach (var job in Jobs().Where(j => j.Node == node && j.State == "leased")) ExpireLease(job, "node revoked");
        audit.Write(by, "fleet.revoke", node, "certificate refused from now on");
    }

    /// <summary>The node a verified client certificate belongs to, or why it is refused. The call must come in on the node's own relay port.</summary>
    public (FleetNode? Node, string? Problem) NodeFor(X509Certificate2? cert, int localPort)
    {
        if (cert is null || !ca.VerifyClient(cert)) return (null, "a client certificate from the fleet CA is required");
        var fingerprint = FleetCa.Fingerprint(cert);
        var node = db.Query("SELECT name, slot, labels_json, never_touch, state, fingerprint, version, capacity, last_heartbeat FROM fleet_nodes WHERE fingerprint=$f;", MapNode, ("$f", fingerprint)).FirstOrDefault();
        if (node is null) return (null, "unknown certificate");
        if (node.State is not ("enrolled" or "draining")) return (null, $"node {node.Name} is {node.State}");
        if (localPort != config.Fleet.ControllerPort + node.Slot) return (null, $"node {node.Name} must use its own relay port");
        return (node, null);
    }

    /// <summary>Records the heartbeat and returns the node's gateway token, which only ever travels over the node's mutual TLS channel.</summary>
    public string? Heartbeat(FleetNode node, string? version, int capacity)
    {
        db.Exec("UPDATE fleet_nodes SET last_heartbeat=$t, version=$v, capacity=$c WHERE name=$n;",
            ("$t", Now), ("$v", version is { Length: <= 64 } ? version : null), ("$c", Math.Clamp(capacity, 0, 32)), ("$n", node.Name));
        var file = paths.Bot(NodeTokenPrefix + node.Name).TokenFile;
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    // ---------- jobs ----------

    public IReadOnlyList<FleetJob> Jobs() => db.Query("SELECT id, idempotency_key, kind, spec_json, spec_hash, state, approval_id, approved_by, requested_by, node, lease_id, lease_expires_at, fencing, attempts, reason FROM fleet_jobs ORDER BY created_at;", MapJob);

    public FleetJob? Job(string id) => db.Query("SELECT id, idempotency_key, kind, spec_json, spec_hash, state, approval_id, approved_by, requested_by, node, lease_id, lease_expires_at, fencing, attempts, reason FROM fleet_jobs WHERE id=$i;", MapJob, ("$i", id)).FirstOrDefault();

    private static FleetJob MapJob(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), JsonNode.Parse(r.GetString(3)) as JsonObject ?? [],
        r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : Parse(r.GetString(11)), r.GetInt64(12), r.GetInt32(13), r.IsDBNull(14) ? null : r.GetString(14));

    private static IReadOnlyList<(string Id, bool Outward)> Steps(JsonObject spec)
        => spec["steps"] is JsonArray a && a.Count > 0
            ? [.. a.OfType<JsonObject>().Select(s => (s["id"]?.GetValue<string>() ?? "", s["outward"] is JsonValue v && v.TryGetValue<bool>(out var o) && o))]
            : [("run", false)];

    /// <summary>
    /// Adds a job a person wrote. approved_by in the spec is dropped: it is
    /// written only from an approval record. Read-only kinds run under the
    /// standing policy; every other kind waits for a person's card bound to the
    /// job hash.
    /// </summary>
    public FleetJob AddJob(JsonObject spec, string by)
    {
        Guard();
        spec = (JsonObject)spec.DeepClone();
        if (spec.Remove("approved_by")) audit.Write(by, "fleet.approved_by_ignored", spec["idempotency_key"]?.ToString() ?? "?", "approved_by is written only from an approval record");
        spec.Remove("approval_id");
        var key = spec["idempotency_key"]?.GetValue<string>() ?? throw new FleetException("idempotency_key is required.");
        if (!Key().IsMatch(key)) throw new FleetException("idempotency_key must be 1 to 200 letters, digits and . _ : / -.");
        var kind = spec["kind"]?.GetValue<string>() ?? throw new FleetException("kind is required.");
        if (!Kinds.Contains(kind)) throw new FleetException("kind must be one of " + string.Join(", ", Kinds) + ".");
        Normalize(spec);
        var steps = Steps(spec);
        if (steps.Any(s => !Item().IsMatch(s.Id)) || steps.Select(s => s.Id).Distinct().Count() != steps.Count) throw new FleetException("Each step needs its own id (lower case letters, digits, dots, dashes, underscores).");
        if (ReadOnlyKinds.Contains(kind) && steps.Any(s => s.Outward)) throw new FleetException($"A {kind} job cannot have outward steps.");
        foreach (var name in SecretNames(spec))
            if (!Item().IsMatch(name)) throw new FleetException($"Secret name {name} is not valid.");
        if (spec["target_node"] is JsonValue tv && !NodeName().IsMatch(tv.GetValue<string>())) throw new FleetException("target_node is not a node name.");
        if (Jobs().Any(j => j.Key == key)) throw new FleetException($"A job with idempotency_key {key} already exists; a retry reuses it, never a new job.");
        var json = spec.ToJsonString();
        var hash = SecureFile.Sha256Hex(json);
        var id = "job_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var readOnly = ReadOnlyKinds.Contains(kind);
        string? approval = null;
        if (!readOnly)
        {
            var card = new ApprovalStore(db, clock, Redactor.Shared).Create(new ApprovalRequest(ApprovalKinds.FleetJob, "fleet", $"Fleet job {kind} {key}",
                new JsonObject { ["job_id"] = id, ["spec_hash"] = hash, ["spec"] = JsonNode.Parse(json) }, "person", by, "person",
                SessionId: "fleet", PauseId: id, Ttl: TimeSpan.FromHours(24), PersonOnly: true, Action: $"run fleet job {key} ({kind}), spec {hash[..16]}"));
            approval = card.Id;
        }
        db.Exec("""
            INSERT INTO fleet_jobs(id, idempotency_key, kind, spec_json, spec_hash, state, approval_id, approved_by, requested_by, created_at, updated_at)
            VALUES ($i,$k,$kind,$s,$h,$st,$a,$ab,$by,$t,$t);
            """, ("$i", id), ("$k", key), ("$kind", kind), ("$s", json), ("$h", hash), ("$st", readOnly ? "queued" : "waiting_approval"),
            ("$a", approval), ("$ab", readOnly ? "policy:read-only" : null), ("$by", by), ("$t", Now));
        audit.Write(by, "fleet.job_add", id, $"{kind} {key} spec {hash[..16]}{(readOnly ? " (read-only policy)" : $", card {approval}")}");
        return Job(id)!;
    }

    /// <summary>
    /// Job files written in YAML carry every value as text. outward must end up a
    /// real true or false, or an outward step could be read as safe to repeat.
    /// </summary>
    private static void Normalize(JsonObject spec)
    {
        if (spec["steps"] is JsonNode stepsNode)
        {
            if (stepsNode is not JsonArray steps) throw new FleetException("steps is a list of {id, outward}.");
            foreach (var step in steps)
            {
                if (step is not JsonObject so || so["id"] is not JsonValue idv || !idv.TryGetValue<string>(out _)) throw new FleetException("Each step is an object with an id.");
                switch (so["outward"])
                {
                    case null: so["outward"] = false; break;
                    case JsonValue v when v.TryGetValue<bool>(out _): break;
                    case JsonValue v when v.TryGetValue<string>(out var text) && text is "true" or "false": so["outward"] = text == "true"; break;
                    default: throw new FleetException($"Step {idv} outward must be true or false.");
                }
            }
        }
        if (spec["limits"]?["max_attempts"] is JsonValue mv && mv.TryGetValue<string>(out var ms))
            spec["limits"]!["max_attempts"] = int.TryParse(ms, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FleetException("limits.max_attempts is a number.");
        if (spec["allow_never_touch"] is not null) throw new FleetException("allow_never_touch is not available; never-touch nodes take no fleet work.");
    }

    public void Cancel(string id, string by)
    {
        var job = Job(id) ?? throw new FleetException($"No job {id}.");
        if (job.State is "done" or "failed" or "cancelled") throw new FleetException($"Job {id} is already {job.State}.");
        db.Exec("UPDATE fleet_jobs SET state='cancelled', lease_id=NULL, lease_expires_at=NULL, reason=$r, updated_at=$t WHERE id=$i;", ("$r", "cancelled by " + by), ("$t", Now), ("$i", id));
        audit.Write(by, "fleet.job_cancel", id, "");
    }

    private static IEnumerable<string> SecretNames(JsonObject spec) => spec["secrets"] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "") : [];

    /// <summary>Delivers decided cards and ends expired leases. A lease that ended after an outward step started makes the job lost: a person decides.</summary>
    public IReadOnlyList<string> Tick()
    {
        var lines = new List<string>();
        var approvals = new ApprovalStore(db, clock, Redactor.Shared);
        foreach (var job in Jobs().Where(j => j.State == "waiting_approval" && j.ApprovalId is not null))
        {
            var card = approvals.Get(job.ApprovalId!);
            if (card is null) continue;
            if (card.IsAllowed && card.ApproverKind == "person" && card.PayloadJson()["spec_hash"]?.GetValue<string>() == job.SpecHash)
            {
                db.Exec("UPDATE fleet_jobs SET state='queued', approved_by=$a, updated_at=$t WHERE id=$i AND state='waiting_approval';", ("$a", "person:" + card.Approver), ("$t", Now), ("$i", job.Id));
                audit.Write("fleet", "fleet.job_approved", job.Id, $"approved by person:{card.Approver} on card {card.Id}");
                lines.Add($"fleet job {job.Id} approved");
            }
            else if (card.Status is ApprovalStatus.Denied or ApprovalStatus.Expired or ApprovalStatus.Cancelled or ApprovalStatus.HardDenied
                || (card.IsAllowed && card.ApproverKind != "person") || (card.Status == ApprovalStatus.Pending && card.ExpiresAt <= clock.UtcNow))
            {
                db.Exec("UPDATE fleet_jobs SET state='cancelled', reason=$r, updated_at=$t WHERE id=$i;", ("$r", "card " + card.Status), ("$t", Now), ("$i", job.Id));
                lines.Add($"fleet job {job.Id} cancelled ({card.Status})");
            }
        }
        foreach (var job in Jobs().Where(j => j.State == "leased" && j.LeaseExpiresAt <= clock.UtcNow)) lines.Add(ExpireLease(job, "lease expired"));
        return lines;
    }

    private string ExpireLease(FleetJob job, string why)
    {
        // Only an outward step that started and never finished makes the job lost; finished steps are skipped on a retry.
        var outwardStarted = Convert.ToInt64(db.Scalar("""
            SELECT COUNT(*) FROM fleet_ledger i WHERE i.job_id=$i AND i.outward=1 AND i.phase='intent'
              AND NOT EXISTS (SELECT 1 FROM fleet_ledger d WHERE d.job_id=i.job_id AND d.step_id=i.step_id AND d.phase='done');
            """, ("$i", job.Id)), CultureInfo.InvariantCulture) > 0;
        var max = job.Spec["limits"]?["max_attempts"] is JsonValue m && m.TryGetValue<int>(out var mv) ? Math.Clamp(mv, 1, 5) : 2;
        var state = outwardStarted ? "lost" : job.Attempts >= max ? "failed" : "queued";
        db.Exec("UPDATE fleet_jobs SET state=$s, node=CASE WHEN $s='queued' THEN NULL ELSE node END, lease_id=NULL, lease_expires_at=NULL, reason=$r, updated_at=$t WHERE id=$i AND state='leased';",
            ("$s", state), ("$r", why), ("$t", Now), ("$i", job.Id));
        audit.Write("fleet", "fleet.lease_ended", job.Id, $"{why} on {job.Node}; job is now {state}");
        if (state == "lost")
            notices.Add("alarm", null, $"Fleet job {job.Id} lost its node after an outward step started. Check the outside system before anything runs again: oma-bot fleet job show {job.Id}", "fleet_lost:" + job.Id);
        return $"fleet job {job.Id} {state} ({why})";
    }

    private bool Matches(FleetJob job, FleetNode node)
    {
        if (node.NeverTouch) return false;
        if (job.Spec["never_nodes"] is JsonArray never && never.Any(n => n?.GetValue<string>() == node.Name)) return false;
        if (job.Spec["target_node"] is JsonValue t && t.GetValue<string>() != node.Name) return false;
        if (job.Spec["selector"]?["labels"] is JsonObject want)
            foreach (var (k, v) in want)
                if (!node.Labels.TryGetValue(k, out var have) || have != v?.GetValue<string>()) return false;
        return true;
    }

    /// <summary>The next job this node may take, claimed atomically, or null.</summary>
    public FleetLease? Lease(FleetNode node)
    {
        if (config.Fleet.KillSwitch || node.State != "enrolled") return null;
        var busy = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM fleet_jobs WHERE node=$n AND state='leased';", ("$n", node.Name)), CultureInfo.InvariantCulture);
        if (busy >= Math.Max(1, node.Capacity)) return null;
        foreach (var job in Jobs().Where(j => j.State == "queued" && j.ApprovedBy is not null && Matches(j, node)))
        {
            var lease = "lse_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var expires = clock.UtcNow + TimeSpan.FromSeconds(config.Fleet.LeaseSeconds);
            if (db.Exec("UPDATE fleet_jobs SET state='leased', node=$n, lease_id=$l, lease_expires_at=$e, fencing=fencing+1, attempts=attempts+1, updated_at=$t WHERE id=$i AND state='queued';",
                ("$n", node.Name), ("$l", lease), ("$e", Iso(expires)), ("$t", Now), ("$i", job.Id)) != 1) continue;
            var fresh = Job(job.Id)!;
            audit.Write("fleet", "fleet.lease", job.Id, $"to {node.Name}, fencing {fresh.Fencing}");
            var spec = (JsonObject)fresh.Spec.DeepClone();
            spec["approved_by"] = fresh.ApprovedBy;
            return new FleetLease(job.Id, lease, fresh.Fencing, expires, spec);
        }
        return null;
    }

    /// <summary>The job, if this node holds its live lease with this fencing token.</summary>
    private (FleetJob? Job, FleetReply? Refusal) Holding(FleetNode node, string jobId, long fencing)
    {
        var job = Job(jobId);
        if (job is null) return (null, new(false, 404, "No such job."));
        if (job.State != "leased" || job.Node != node.Name || job.Fencing != fencing || job.LeaseExpiresAt <= clock.UtcNow)
        {
            audit.Write("fleet", "fleet.stale_write", jobId, $"from {node.Name} with fencing {fencing}; current {job.Fencing} on {job.Node ?? "none"} ({job.State})");
            return (null, new(false, 409, "Stale lease or fencing token; stop work on this job."));
        }
        return (job, null);
    }

    public FleetReply Renew(FleetNode node, string jobId, long fencing)
    {
        var (job, refusal) = Holding(node, jobId, fencing);
        if (job is null) return refusal!;
        db.Exec("UPDATE fleet_jobs SET lease_expires_at=$e, updated_at=$t WHERE id=$i;", ("$e", Iso(clock.UtcNow + TimeSpan.FromSeconds(config.Fleet.LeaseSeconds))), ("$t", Now), ("$i", jobId));
        return new(true, 200, "Renewed.");
    }

    /// <summary>
    /// The step ledger. intent before a step and done after it, each once. An
    /// outward step that has an intent and no done from an earlier attempt is
    /// never started again: the job turns lost and a person checks.
    /// </summary>
    public FleetReply Step(FleetNode node, string jobId, string stepId, string phase, long fencing, string? externalId)
    {
        var (job, refusal) = Holding(node, jobId, fencing);
        if (job is null) return refusal!;
        var step = Steps(job.Spec).FirstOrDefault(s => s.Id == stepId);
        if (step.Id is null) return new(false, 404, "No such step.");
        if (phase is not ("intent" or "done")) return new(false, 400, "phase is intent or done.");
        bool Has(string p) => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM fleet_ledger WHERE job_id=$i AND step_id=$s AND phase=$p;", ("$i", jobId), ("$s", stepId), ("$p", p)), CultureInfo.InvariantCulture) > 0;
        if (phase == "intent")
        {
            if (Has("done")) return new(true, 200, "Already done; skip it.", new JsonObject { ["done"] = true });
            if (Has("intent"))
            {
                if (!step.Outward) return new(true, 200, "Not outward; run it again.");
                db.Exec("UPDATE fleet_jobs SET state='lost', lease_id=NULL, lease_expires_at=NULL, reason=$r, updated_at=$t WHERE id=$i;", ("$r", $"outward step {stepId} started before and did not finish"), ("$t", Now), ("$i", jobId));
                audit.Write("fleet", "fleet.outward_repeat_refused", jobId, $"step {stepId} from {node.Name}");
                notices.Add("alarm", null, $"Fleet job {jobId}: outward step {stepId} started on an earlier attempt and never finished. It will not run again until a person checks the outside system.", "fleet_lost:" + jobId);
                return new(false, 409, "An earlier attempt started this outward step and did not finish. It is never repeated blindly; stop.");
            }
        }
        else if (!Has("intent")) return new(false, 409, "done needs an intent first.");
        else if (Has("done")) return new(true, 200, "Already recorded.");
        db.Exec("INSERT INTO fleet_ledger(job_id, step_id, phase, outward, node, fencing, external_id, at) VALUES ($i,$s,$p,$o,$n,$f,$x,$t);",
            ("$i", jobId), ("$s", stepId), ("$p", phase), ("$o", step.Outward ? 1 : 0), ("$n", node.Name), ("$f", fencing), ("$x", externalId is { Length: <= 200 } ? Redactor.Shared.Redact(externalId) : null), ("$t", Now));
        if (step.Outward) audit.Write("fleet", "fleet.outward_" + phase, jobId, $"step {stepId} on {node.Name}{(externalId is null ? "" : ", external id recorded")}");
        return new(true, 200, "Recorded.");
    }

    public FleetReply Complete(FleetNode node, string jobId, long fencing, JsonObject result)
    {
        var (job, refusal) = Holding(node, jobId, fencing);
        if (job is null) return refusal!;
        var missing = Steps(job.Spec).Where(s => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM fleet_ledger WHERE job_id=$i AND step_id=$s AND phase='done';", ("$i", jobId), ("$s", s.Id)), CultureInfo.InvariantCulture) == 0).Select(s => s.Id).ToList();
        if (missing.Count > 0) return new(false, 409, "Steps not done: " + string.Join(", ", missing));
        result.Remove("approved_by");
        var text = Redactor.Shared.Redact(result.ToJsonString());
        if (text.Length > MaxResultBytes) return new(false, 413, "The result is too large; upload it as an artifact.");
        db.Exec("UPDATE fleet_jobs SET state='done', result_json=$r, lease_id=NULL, lease_expires_at=NULL, updated_at=$t WHERE id=$i;", ("$r", text), ("$t", Now), ("$i", jobId));
        audit.Write("fleet", "fleet.job_done", jobId, $"on {node.Name}");
        return new(true, 200, "Done.");
    }

    public static string ArtifactDir(RosterPaths p, string jobId) => Path.Combine(p.Home, "fleet", "artifacts", jobId);

    /// <summary>Stores an artifact only when its SHA-256 matches what the node claims.</summary>
    public FleetReply Artifact(FleetNode node, string jobId, long fencing, string name, byte[] data, string? claimedSha256)
    {
        var (job, refusal) = Holding(node, jobId, fencing);
        if (job is null) return refusal!;
        if (!Item().IsMatch(name)) return new(false, 400, "Artifact names are lower case letters, digits, dots, dashes and underscores.");
        if (data.LongLength > MaxArtifactBytes) return new(false, 413, "The artifact is too large.");
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        if (!SecureFile.FixedTimeEquals(sha, claimedSha256?.Trim().ToLowerInvariant()))
        {
            audit.Write("fleet", "fleet.artifact_refused", jobId, $"{name} from {node.Name}: digest mismatch");
            return new(false, 422, "The digest does not match the bytes received.");
        }
        var dir = ArtifactDir(paths, jobId);
        SecureFile.EnsureDir(dir);
        SecureFile.WriteAllBytesAtomic(Path.Combine(dir, name), data);
        db.Exec("INSERT OR IGNORE INTO fleet_artifacts(job_id, name, sha256, size, node, at) VALUES ($i,$n,$s,$z,$node,$t);",
            ("$i", jobId), ("$n", name), ("$s", sha), ("$z", data.LongLength), ("$node", node.Name), ("$t", Now));
        audit.Write("fleet", "fleet.artifact", jobId, $"{name} {sha[..16]} {data.LongLength} bytes from {node.Name}");
        return new(true, 200, "Stored.", new JsonObject { ["sha256"] = sha });
    }

    public static string JobSecretDir(RosterPaths p) => Path.Combine(FleetCa.Dir(p), "jobs");

    /// <summary>The job's secrets, only to the node holding its live lease. Names go to the audit log; values never do.</summary>
    public FleetReply Secrets(FleetNode node, string jobId, long fencing)
    {
        var (job, refusal) = Holding(node, jobId, fencing);
        if (job is null) return refusal!;
        var files = new JsonObject();
        foreach (var name in SecretNames(job.Spec))
        {
            var file = Path.Combine(JobSecretDir(paths), name);
            if (!File.Exists(file) || new FileInfo(file).LinkTarget is not null || !SecureFile.IsPrivate(file)) return new(false, 409, $"Job secret {name} is missing or not private.");
            var value = File.ReadAllText(file).TrimEnd('\n');
            Redactor.Shared.AddKnownSecret(value);
            files[name] = value;
        }
        audit.Write("fleet", "fleet.secret_read", jobId, $"{string.Join(", ", SecretNames(job.Spec))} to {node.Name}");
        return new(true, 200, "Secrets.", new JsonObject { ["files"] = files });
    }
}
