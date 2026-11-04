# OMA Fleet Flow Analysis

Read-only analysis for the OMA roster plan (OMA-BOT-ROSTER-PLAN.md). Written Nov 3, 2026 (Phoenix time). Sources: the newest transcript pages of SHS-Form-Submit (positions about 1650 to 1806, Oct 27 to Nov 1) and Video Generator Project (positions about 9300 to 9400, Nov 1 to Nov 2), plus the roster plan sections 4.3, 4.4 and 6.1. No bot was messaged, no repo was touched. Credentials, password file names, tokens, personal emails and share links are left out on purpose.

Limits: only the most recent pages were read. Older design history (how echo20's video app or echo11's orchestrator were first built) is inferred from how the bots use them, and is marked "inferred" where that matters.

---

## 1. How each project works end to end

### 1.1 SHS-Form-Submit (law firm finder and enrichment)

**What it is.** A .NET system (projects named Shs.Api, Shs.Core, Shs.Tests) that scrapes law firm directories, builds a curated list of firm domains, finds and enriches contacts, scores email quality, and holds a form submit feature that has never run live (`LiveSubmit=false`).

**Machines and roles**
| Machine | Role |
|---|---|
| Shared box (the bot's own Linux box) | Runs the bot, its executor subagents, the directory scrapers (FindLaw, Justia, Avvo, state bar), the domain recompile step, and helper scripts. Reaches echo11 over SSH. |
| echo11 | The worker server. Runs the whole SHS stack in Docker: ui, api, orchestrator, postgres, searxng, scrapling, gateway, alloy, grafana, loki, three openmono containers, and llama-server with a Qwen 3.6 35B model. About 25 GB RAM plus 24 GB swap. Clock is UTC. |
| echo20, pandora | Other servers that SHS work must never touch. Every SHS brief repeats "echo11 only, never echo20 or pandora". |
| Cursor cloud agent | Writes all code changes on a branch, runs the test suite, pushes to GitLab, opens or updates a merge request. |
| GitLab | Source of truth for the repo, branches and merge requests. |

**Job queue.** The orchestrator on echo11 runs a worker queue in postgres with named job types (finder, solo enrichment, MX check, card import, hydrate, site-text, Qwen profiling, CA/WA deep scan, TN sweep). Concurrency is set by slots in a git-ignored env file (`QUEUE_OTHER_SLOTS=2`). Each job type has start and stop endpoints and a status endpoint, and some run in "continuous" mode. Long work is batched (2000 or 8000 row batches) and restartable.

**Handoffs and execution**
1. Spencer asks in chat, or a routine finds something.
2. The bot investigates read-only first (SELECTs, code reading) and reports a diagnosis with numbers and a suggested fix.
3. Spencer says "go ahead".
4. The bot sends a tightly scoped brief to the cloud agent: exact branch and base commit, required changes, migration rules (additive, idempotent, batched), tests to add, known failing tests, hard rules (LiveSubmit stays false, no secrets, no dashes). The cloud agent replies with commit hash, files, migration number and test results.
5. The bot launches an executor subagent with a deploy brief to echo11 only.

**Deploy flow on echo11 (the "brick" deploy)**
1. Pre-check: memory, swap, and the state of every job, recorded so they can be restored.
2. Backup: pg_dump to a dated file, tag current images `:pre-<commit>-<date>`, and copy any column that the change rewrites into a backup table.
3. `git fetch` plus `git reset --hard <commit>`, build images one at a time with a low-memory abort (stop a job temporarily if available memory would drop under about 0.7 GB).
4. `brick-reinstall.sh --no-pull`, then `verify-brick` (14 checks must pass).
5. Confirm config survived (slots in the env file, LiveSubmit false), watch the migration finish.
6. Restart jobs that were running before, one at a time while watching memory. Start the backfill and watch it.
7. Verify with before and after SELECTs and samples, and flag anything that looks wrong.
8. Report: deploy result, checks, migration time, counts, memory, job states, and any action taken that was not asked for (for example cancelling two stale queue jobs and restarting only the orchestrator, with copies saved to a backup table first).

**Routines.** `law-firm-scrape-keepalive` runs about every 6 hours. It checks the scrapers are alive and their logs are moving, recompiles unique domains, imports only the new ones to echo11, checks the finder, writes a line to a notes log and memory, saves the last imported count to a state file, and messages Spencer only when something is broken or needs a decision. Noticed lessons are carried forward (run the import without sudo because that is safer, connect with the right user because the SSH default points elsewhere).

**Approvals.** Code changes, turning jobs back on, outreach, and anything that needs an app sign-in wait for Spencer. Routines report the decision instead of making it (the finder stayed off for days because turning it on was Spencer's call). Any email or form outreach needs Spencer to approve the exact message.

**Failure handling.** Fail visible, not silent. Executors write output to files, use timeouts, kill only their own processes, never print secrets, stop and report if a safety check blocks a sudo step. Rollback assets (dump, image tags, backup tables) exist before every change. Known old test failures are listed so they do not cause false alarms.

### 1.2 Video Generator Project (SHS YouTube videos)

**What it is.** A video app running on echo20 (API on a local port there) that turns an approved script into a finished video: narration, B-roll (Wan model), on-camera clips lip synced through a paid Sync service, a shared extro, final render. The bot handles research, script, app steps, monitoring, Drive upload and the email.

**Machines and roles**
| Machine | Role |
|---|---|
| Shared box | The bot and executors. Per-job work folders (`/workspace/<job>/`) with `STATUS.md`, links, sources, script, narration and the JSON request bodies used for each API call. Reaches echo20 through a small wrapper script that runs commands there. |
| echo20 | The render worker: job queue, rendering, GPU work, the review tool, the sync cost endpoint with a daily dollar cap ($6). |
| Gmail and Google Drive | Source threads (Spencer's link threads) and delivery (upload, share to two named readers, link sharing off). |

**Job lifecycle on echo20.** Create script, fetch sources in the app (12 sources), run the review tool until 0 flags (drop figures that do not match a source rather than fighting the tool), approve, queue the job exactly once. States seen: Planning, Rendering (segments of B-roll), WaitingForSync, then Sync, then Done. A retry endpoint (`POST /api/jobs/{id}/retry`) reuses finished pieces.

**Idempotency rules.** These are the most important lesson for the fleet. Every brief says "queued ONCE as job X. Never queue again." and "script created, never create again". IDs are written to `job-id.txt` and `script-id.txt` and to STATUS.md the moment they exist. Retry is allowed once, only for a retryable error, and only through the app's retry endpoint.

**Handoff and resume.** Executors keep STATUS.md current so an interrupted executor can be resumed with "continue from STATUS.md". In this window three executor runs were interrupted. Each time the bot checked the job directly on echo20 (status, progress, error, sync cost, uptime) and resumed the same executor with a short brief. The render itself kept running on echo20 because the worker owns the job, not the bot.

**Approvals and money.** The job stops at WaitingForSync. The bot reads the cost quote (estimate, spent today, cap left, whether the cap would block) and asks Spencer. Nothing is spent without his OK. The email goes out only as a draft card that Spencer sends himself.

**Verification and report.** md5 match between the echo20 output and the uploaded file, permissions checked after sharing, narration in the email matched word for word against the app, sources checked against what the app actually fetched (an out of date sources file was spotted and not used), speech check notes for the brand name, no dashes.

---

## 2. The common flow Spencer follows

1. **Intake.** A request arrives from Spencer, a link thread, or a routine.
2. **Read-only look first.** Investigate on the worker with reads only, and come back with numbers and a proposed change.
3. **Approval gate.** Spencer approves the change, the spend, or the outward action. Routines never make his decisions for him.
4. **Scoped brief.** The main bot writes a precise brief: one target server, explicit "never touch" servers, exact commit or job IDs, hard rules, what to report.
5. **Delegate.** Code goes to a cloud agent on a branch with tests and a merge request. Operations go to an executor that reaches one worker.
6. **Snapshot before change.** Record job states, take backups, tag images, copy columns that will change.
7. **Execute once.** Create or queue once, record the ID immediately, use the worker's own queue and slots, stay inside memory and cost limits.
8. **Durable progress log.** STATUS.md, notes logs and state files so any run can be resumed by another.
9. **Monitor with long waits.** Check worker health and job state on a schedule, retry once only for retryable errors.
10. **Pause at gates.** Stop at money, outreach or risky steps and quote the cost or show the draft.
11. **Verify.** Health checks (verify-brick), before and after counts, samples, checksums, permission checks.
12. **Report.** Lead with the result, list anything done that was not asked for, list decisions waiting on Spencer.
13. **Quiet routines.** Keepalives run often, write a log line, and only wake Spencer for breakage or a real decision.
14. **Carry lessons forward.** Safer ways found during a run become the default for later runs.

---

## 3. Mapping to an OMA fleet design

Goal: head-honcho (the main bot on the roster host or web instance) plus sub bots plan and approve work, and about 20 OMA servers execute it. The worker owns the job, as echo20 and echo11 do today, so a bot restart never loses or repeats work.

### 3.1 Components
| Component | Where | Notes |
|---|---|---|
| Fleet controller | Inside the roster service (bot/src/Omabot), new module | Registry, job store, lease manager, approval broker, audit. Only head-honcho's roster service runs it. |
| Node agent (`omabot-node`) | Each OMA server, beside agent-host | New add-on, not an edit to OMA. Pulls jobs, runs them through the local OMA agent over ACP on loopback, and uses the local agent-host for approved host commands. |
| agent-host | Each OMA server, unchanged | Stays the local executor for host commands with its deny list, sudo policy and audit. It listens only on loopback and the Docker bridge (plan section 4.4), so it must not be exposed to the fleet. The node agent is the fleet-facing piece. If Spencer wants agent-host itself to become the node daemon, that is a proposal for the OMA maintainers, not a roster change. |
| Roster gateway | Roster host | Single doorway to inference for every node. Nodes get a per-node token, never the real inference key. |
| Inference server | Spencer's separate box | Always remote. Only the gateway talks to it. |
| Artifact store | Roster host disk or an S3 compatible store on the LAN (MinIO, inferred choice) | Content addressed results, checksums. |

### 3.2 Fleet registry
One row per node: node id, display name, Tailscale name, enrollment date, certificate fingerprint, OMA version and image digest, labels, capacity, health, last heartbeat, drain flag, owner, and a "never touch" flag for protected machines (like echo20 and pandora today for SHS work). Kept in the roster SQLite store, with every change in the audit log. A team YAML section `fleet:` declares the expected nodes, and `oma-bot fleet apply` shows a plan before changing anything (same pattern as `team apply`).

### 3.3 Labels and capacity
- Labels: `gpu=true`, `gpu.vram=24`, `ram=25g`, `role=render`, `role=scrape`, `project=shs-form-submit`, `zone=office`, `docker=true`.
- Capacity: job slots per node (like `QUEUE_OTHER_SLOTS=2`), memory floor (refuse new work under a threshold like the 0.7 GB rule), disk floor, and optional cost caps per job type (like the $6 daily Sync cap).
- Jobs carry selectors (`requires: role=render`, `avoid: never-touch`) and a resource request. The controller only offers a job to a node whose labels match and which reports free capacity.

### 3.4 Job spec
```yaml
job_id: 01J...            # ULID created by the controller
idempotency_key: shs/deploy/234f244/echo11   # stable across retries
project: shs-form-submit
kind: deploy | run_playbook | routine_check | render | backfill | readonly_query
requested_by: head-honcho
approved_by: spencer      # or "policy:<rule id>" for pre-approved read-only kinds
approval_id: apv_...
selector: {labels: {role: scrape, project: shs-form-submit}}
target_node: echo11       # optional pin
never_nodes: [echo20, pandora]
inputs: {repo: gitlab:..., commit: 234f244, playbook: brick-deploy}
steps:                    # each step has its own key and an outward flag
  - {id: snapshot, outward: false}
  - {id: reset_and_build, outward: false}
  - {id: reinstall, outward: false}
  - {id: verify, outward: false}
gates: [{before: sync, type: cost, max_usd: 2.0}]
limits: {timeout: 2h, max_attempts: 2, memory_floor_mb: 700}
report: {to: head-honcho, fields: [result, checks, counts, unasked_actions]}
```
The bot never fills `approved_by`. The controller writes it from the approval record, so a model cannot approve its own job (same idea as the delegation envelope in the Rakazo notes).

### 3.5 Pull versus push
Recommendation: **pull with long polling**. Each node agent opens an outbound HTTPS connection to the controller over Tailscale, asks for work matching its labels and capacity, and holds the request open until work arrives or 30 seconds pass. Reasons: nodes need no inbound port, a node behind NAT works, a node that is busy simply does not ask, and the controller never needs credentials for 20 machines (today the bots hold SSH passwords for echo11, which is the pattern to retire). Push is kept only for one control message, "cancel or drain", delivered on the next poll or the heartbeat reply.

### 3.6 Lease protocol
1. `POST /fleet/v1/lease` with node id, free capacity, labels. The controller returns one job and a lease (lease id, expiry in 60 seconds, fencing token that increases each time).
2. The node renews with `POST /fleet/v1/lease/{id}/renew` every 20 seconds, sending step progress.
3. Each step result is posted with the fencing token. The controller rejects a write with an old token, so a node that lost its lease (network split) cannot overwrite a newer attempt.
4. On lease expiry the job goes to `lost`. It is not reassigned automatically if any step marked `outward: true` had started. It waits for head-honcho, and head-honcho asks Spencer (same as "never queue again").
5. Completion: `POST /fleet/v1/jobs/{id}/complete` with result, artifact digests, checks passed, and a list of unasked actions.

### 3.7 Idempotency so outward actions never repeat
- Every job and step has an idempotency key. The node keeps a local ledger (SQLite) of step keys it finished. The controller keeps the same ledger centrally.
- Outward steps (send email, submit a form, spend money, post, push, deploy to production, create an app job that bills) use a two phase record: `intent` written before the call, `done` with the external id after. If a run finds `intent` without `done`, it must check the outside system first (for example read the job list on the worker, as the video bot did) and never call again blindly.
- External ids are saved at once (like `job-id.txt` and `script-id.txt`).
- Retries reuse finished pieces through the worker's own retry API, at most once, only for retryable error classes (timeout, 502, 503, 504, connection reset). Never for 4xx validation errors or a cost cap block.

### 3.8 Remote inference through the gateway
Node agents run OMA with `OPENMONO_ENDPOINT` pointing at the gateway's Tailscale address and `OPENMONO_API_KEY` set to a per-node gateway token. The gateway queues by the inference server's slot count, tags each request with node, bot and job, and enforces per-node and per-job token budgets. If inference is unreachable the node pauses model steps, keeps leases alive for code-only steps, and reports `inference_unreachable`. Nodes that host their own model (echo11 runs llama-server today) may register it as an extra backend in the gateway, but the default is always the remote server.

### 3.9 Results and artifacts
Small results go inline in the completion call (JSON, size capped). Large outputs (videos, dumps, CSVs, logs) are uploaded to the artifact store with a SHA-256 digest, and the completion carries only the digest and size. The controller verifies the digest (same as the md5 check between echo20 and Drive). Delivery outward (Drive share, email) happens only from head-honcho after approval, never from a node.

### 3.10 Health checks
- Heartbeat every 15 seconds: uptime, load, free memory and swap, disk, Docker containers up, OMA agent version, agent-host status (`/status` on loopback), gateway reachability, job slots in use.
- Deep check on demand or daily: a `verify` playbook per node role (like verify-brick's 14 checks).
- States: healthy, degraded (memory under floor, swap rising, a container unhealthy), unreachable (3 missed heartbeats), draining, quarantined (failed security self-check or unexpected version).
- Idle resource warnings (like the idle llama-server holding RAM) go to head-honcho's daily summary, not as alerts.

### 3.11 Rollout and updates of OMA on nodes
Copy the brick flow, staged:
1. Pin a release (image digest plus git commit), signed by Spencer's release key.
2. Canary: one node, drained first. Snapshot (image tags `:pre-<version>`, config backup, data backup where relevant).
3. Pull image, install, run the verify playbook, run a smoke job.
4. If it passes, waves of 25 percent, each wave waiting for all checks. Stop on the first failure.
5. Automatic rollback to the tagged images on failed verify. Report per node.
Git-ignored settings (like slot counts) are checked after every update, since the SHS deploys had to re-confirm them each time.

### 3.12 Secrets distribution
- Nodes get no shared secrets. Each node has its own key pair from enrollment and its own gateway token.
- Project secrets (API keys a job needs) are delivered per job, scoped and short lived, from a secrets store on the roster host (age or SOPS encrypted files to start, Vault or Infisical later). They are passed as a mounted file to the job container, never as command line arguments (the echo20 process list leak came from that) and never in chat or logs.
- Retire sshpass password files and sudo password piping. Privileged host steps go through agent-host's HostExec with its sudo policy, approved per job.

### 3.13 Audit
Append-only audit log on the controller and on each node: who asked, who approved, which node, which job, step start and end, outward intents and results, unasked actions, secret access (name only, never value), config changes. Each line hashed with the previous one so tampering is visible. Node logs ship to the controller (the echo11 stack already runs alloy, loki and grafana, which can be reused per node).

### 3.14 Failure and retry rules
| Failure | Rule |
|---|---|
| Transient error in a non-outward step | Retry the step up to 2 times with backoff, same node |
| Node lost mid job, no outward step started | Requeue to another matching node after the lease expires |
| Node lost after an outward step started | Mark `lost`, check the outside system, ask head-honcho, who asks Spencer |
| Verify fails after deploy | Automatic rollback to tagged images, report |
| Memory under floor | Pause or refuse new work, optionally stop a low priority job and restore it after (as the deploy brief allowed) |
| Stale jobs holding slots | Report them. Cancel only with approval or a pre-approved rule, and copy them to a backup table first |
| Cost cap would block | Stop at the gate and quote the cost |
| Safety check blocks a privileged step | Stop and report, never work around it |

### 3.15 Approval flow through head-honcho
1. Sub bot or routine proposes a job. Read-only kinds on allowed nodes can run under a standing policy.
2. Anything that changes a node, spends money, or acts outward becomes an approval card for Spencer with the target node, the exact change, the cost quote and the rollback plan.
3. Spencer approves in chat or the web UI. The approval is bound to the job hash, node and expiry (for example 24 hours).
4. The controller writes `approved_by` and releases the job to the lease queue.
5. Gates inside a job (Sync spend) raise a new card mid job. The node holds the lease in a waiting state.
6. Outward messages are always drafts Spencer sends himself unless he set a standing rule.

### 3.16 Observability
A fleet page in the web UI: nodes with health and labels, jobs by state, leases, approvals waiting, cost spent today per cap, inference queue depth at the gateway. Metrics through the existing Prometheus or Grafana style stack, alerts to head-honcho, who applies the quiet rule (wake Spencer only for breakage or decisions). Each project keeps a STATUS view generated from the job store, replacing hand written STATUS.md files while keeping the same content.

---

## 4. Security model for a 20 node fleet

**Network.** All fleet traffic stays on Tailscale with ACL tags: `tag:omacontroller` may accept from `tag:omanode` on the fleet port only; nodes cannot reach each other; nodes reach the gateway port only. Nothing is published to the internet. The web instance exposes only its HTTPS login page.

**Enrollment.**
1. Spencer runs `oma-bot fleet invite --node echo11 --labels ...` on the controller, which creates a one-time code valid for 15 minutes, bound to the node name.
2. On the node, `omabot-node enroll <code>` creates a private key that never leaves the node (TPM backed where available), sends a certificate request.
3. Spencer approves the enrollment card showing the node's Tailscale identity and key fingerprint.
4. The controller's private CA signs a client certificate valid for 30 days, renewed automatically, revocable at once.

**Auth.** Mutual TLS on every fleet call, plus the Tailscale identity must match the registry entry. Leases and fencing tokens are signed by the controller. Gateway tokens are per node and scoped to the gateway only. Recommendation: mTLS over Tailscale rather than signed bearer tokens alone, because a stolen token file is the failure mode seen today.

**Least privilege.** The node agent runs as a non-root user. It can start OMA agent containers and call the local agent-host, which keeps its own deny list and sudo policy. Only the node agent holds the Docker socket on a node. Job containers get no Docker socket, no host network, and egress through the egress gate (LAN, localhost and cloud metadata addresses refused unless the job's allowlist names them).

**Job integrity.** Jobs are signed by the controller. Playbooks and images are pinned by digest. A node refuses jobs it cannot verify, jobs that name it in `never_nodes`, and kinds it is not labeled for.

**Blast radius.** A compromised node sees only its own jobs and per job secrets. It cannot read other nodes' results, approve anything, or reach the inference key. A compromised sub bot can only propose jobs, never approve them. Head-honcho's controller is the crown jewel: separate host or container, backups, its CA key offline or in a hardware key.

**Secrets hygiene.** Rotate the secrets already seen in chat and the echo20 process list before enrolling those machines. No credentials in briefs, transcripts, memory files or logs. Redaction applied to every report.

**Kill switches.** `oma-bot fleet pause` stops all new leases. `fleet drain <node>` and `fleet revoke <node>` (certificate revoked, token revoked, node quarantined).

---

## 5. Phases, tests, risks and open questions

### 5.1 Phases to add to the plan
Suggested after the current phase 5 (handoffs) and phase 6 (approvals), since the fleet reuses both.

- **Phase F0. Inventory and decisions.** List the 20 servers, their roles, hardware, OMA version, which are "never touch" for which projects. Decide controller location and artifact store. Exit: signed off inventory.
- **Phase F1. Registry, enrollment and mTLS.** Controller module, private CA, invite and enroll flow, heartbeat. Exit: 2 nodes enrolled, revocation works, health visible.
- **Phase F2. Node agent and lease protocol, read-only jobs.** Pull with long polling, leases, fencing, `readonly_query` and `routine_check` kinds. Exit: the SHS keepalive runs as a fleet routine on echo11 with no SSH password.
- **Phase F3. Idempotency ledger and outward step rules.** Intent and done records, external id capture, lost job handling. Exit: chaos tests pass with zero duplicate outward actions.
- **Phase F4. Approvals and gates.** Approval cards bound to job hash, cost gates mid job. Exit: a video job pauses at the Sync gate and resumes after approval.
- **Phase F5. Artifacts and results.** Artifact store, digest checks, delivery only from head-honcho.
- **Phase F6. Secrets.** Per job scoped secrets, file mounts, retire sshpass and sudo piping.
- **Phase F7. Rollout and updates.** Signed releases, canary, waves, rollback.
- **Phase F8. Observability and fleet UI.** Fleet page, metrics, quiet alert rules.
- **Phase F9. Scale and soak.** All 20 nodes, 7 day soak, security review.

### 5.2 Tests
- Enrollment: expired code, reused code, wrong node name, unapproved key, revoked certificate, clock skew.
- mTLS: node without certificate, certificate from another CA, valid certificate but wrong Tailscale identity.
- Lease: expiry while running, renewal after expiry, stale fencing token write rejected, two nodes claiming the same job.
- Idempotency: kill the node after intent and before done, kill after done before report, controller restart mid job, network split for 5 minutes. Expect zero repeated outward actions in all cases.
- Selectors: job never lands on a `never_nodes` node, on a node missing labels, or on a node under its memory floor.
- Gateway: node token cannot reach the inference server directly, budget exceeded, inference down.
- Rollout: canary verify fails and rolls back, wave stops on first failure, git-ignored settings survive.
- Secrets: secret never appears in process lists, logs, audit lines, reports or memory (scan tests).
- Approvals: bot tries to set `approved_by`, approval reused on a changed job, approval expired.
- Replays of the real flows: the SHS brick deploy, the keepalive routine, the video job from create to WaitingForSync to Done.

### 5.3 Risks
| Risk | Mitigation |
|---|---|
| Duplicate outward actions after a crash | Two phase ledger, lost state needs a human |
| Controller is a single point of failure | Nodes finish current non-outward steps and hold; backups; later a standby |
| Controller compromise controls the fleet | Separate host, offline CA key, signed jobs, approvals bound to Spencer's login with a passkey |
| One inference server for 20 nodes | Gateway queue, per node budgets, optional local backends |
| Memory pressure on small nodes (echo11 hung before) | Memory floors, one build at a time, refuse work under the floor |
| Drift between nodes (git-ignored settings, versions) | Verify playbook after every change, version in heartbeat |
| Bots acting on Spencer's decisions | Routines report decisions, never make them; standing policies are explicit and listed |
| Scope creep into OMA core | Keep everything in bot/; agent-host changes only as proposals |

### 5.4 Open questions for Spencer
1. Which 20 servers, and which ones are "never touch" for which projects?
2. Should the controller run on the web instance or a separate LAN box? The fleet trusts whatever hosts it.
3. Is Tailscale already on all 20 servers, and may the plan require it?
4. Do you want agent-host extended to be the fleet daemon later (a change to OMA you would review), or keep a separate node agent add-on beside it as proposed?
5. Which job kinds may run without asking each time (read-only checks, keepalives)?
6. Where should artifacts live: the roster host disk, a MinIO box, or Google Drive for finished deliverables only?
7. Should some nodes keep their own local models (echo11's llama-server), or must all inference go through the remote server?
8. What cost caps per day and per job type do you want fleet wide?
9. Should SHS-Form-Submit and Video Generator Project become the first two fleet projects, and in what order?
10. Who besides you may approve jobs, if anyone?
