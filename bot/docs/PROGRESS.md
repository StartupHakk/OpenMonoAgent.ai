# Build progress

Times are America/Phoenix. Test counts are from `dotnet test Omabot.sln` on the build box (Linux, .NET 10.0.403, no Docker).

| Phase | Milestone | Status | Commit | Tests (passed / skipped) |
|---|---|---|---|---|
| 0 | M0 | Recorded; open items need Spencer, the relay and hardware (docs/phase0/VERIFY.md) | 44762ea | none |
| 1 | M1 | Code and tests done; SCN-01 and live Docker checks skipped (no Docker on the build box) | 2314c41 | 101 / 9 |
| 2 | M2 | Code and tests done; LOAD-01 passes against a simulated upstream, live LOAD-01, INT-07 live, INT-13 and LOAD-06 skipped (need the relay and the main box) | 1ba39fc | 141 / 13 |
| 3 | M3 | Code and tests done; SCN-02 and INT-04 skipped (need Docker and a live gateway) | 438de5b | 171 / 15 |
| 4 | M4 | Code and tests done; SCN-03 and SOAK-01 pass in simulated time, their live runs and a real check container are skipped (need Docker, the agent image and the relay) | 6cc9291, 246d9c2 | 213 / 18 |
| 5 | M5 | Code and tests done; SCN-04, SCN-05, SCN-09, SCN-10 and the crash case pass against fake ACP bots; their live runs are skipped (need Docker, the agent image and the relay) | 35dd9f1, 41d9118, d588520 | 239 / 20 |
| 6 | M6 | Code and tests done; SEC-03, SEC-23, SEC-26 to SEC-31, HEALTH-02, SCN-06, SCN-07, SCN-08, SCN-11 pass against fake ACP bots; INT-06 skipped (needs Docker and a running agent-host) | c4bdec8, 8d0140b | 273 / 21 |
| 7 | M7 | Code and tests done; UNIT-EG-01 to 03, SEC-06, SEC-07, SEC-11 pass against a real gate on loopback; SCN-13 skipped (needs Docker and the desktop image) | 270a6e9, f701816 | 292 / 22 |
| 8 | M8 | Code and tests done; UNIT-TC-01 to 07 pass and the task set scores a scripted model; LOAD-04 live skipped (needs the inference server); the success target waits for Spencer | c1100e9, 93ee380 | 307 / 23 |
| 9 | M9 | Code, tests and docs done; SCN-12, LOAD-02, LOAD-03, SOAK-02, SOAK-03, HEALTH-03 and HEALTH-04 pass in simulated time against fake bots and a simulated upstream; LOAD-05, SCN-15, INT-14 live and the live soaks skipped (need Docker, the agent and desktop images, the relay and the inference server) | 5de4ad2, 4c2cbad | 326 / 27 |
| 10 | M10 | Code, tests and docs done; SEC-15, SEC-17, SEC-18, SEC-19 and SEC-23 pass against the real web server, with a TLS test front for the certificate checks; the live front (Caddy and a public certificate) is not run here | 5fb8848, 0c3594c | 341 / 27 |
| 11 | M11 | Code, tests and docs done; SEC-14, SEC-20, SEC-21 and SEC-22 for two users, SEC-25, SEC-27 to SEC-31 in two user rosters and SEC-33 pass; SEC-32 skipped (waits for decision D20). The outside review and Spencer's sign-off are for people | 66ddb5b, 352eb6b | 349 / 28 |
| 12 | M12 | Code, tests and docs done (optional, off by default); FLEET-01 to FLEET-08 pass against the real controller with mutual TLS on loopback and real node agents; the live relay run, rollout (F7) and soak (F9) are skipped; F0 waits for Spencer's inventory | see git log | 358 / 31 |

## Phase 1: bot launcher, supervisor and CLI skeleton

Done:
- Team file parser and resolver (plan 7.13) with refusals by field and line, plan output, apply with one audit row per change, never deletes, export.
- roster.db schema v1 (bots, sessions, failures, audit hash chain with append only triggers, notices, team items).
- Rendering of settings.json, system-prompt.md (hard rules, outside the workspace), OPENMONO.md (capped at 200 lines and 25 KB), permission profiles ask, edits and auto, bash hooks.
- Container spec (launch option B) with labels and the OMA shadowing rules, checked in three layers; Docker Engine client over the unix socket; supervisor host on a 0600 socket with bearer auth.
- Launcher: discovery wait, standing session stored, health probe, backoff 5 s, 30 s, 2 min, 10 min, then disabled with one notice (B19).
- Pass-through gateway: per bot token check, model nickname swap, real key held only by the gateway, plain http upstream refused off loopback (B1), `inference unreachable` state.
- CLI: init, team apply and export, up, down, status, run, events, doctor, enable, disable, notices, audit, serve, render, version.
- Default team: head-honcho (lead) and health (read only, own roster entry).
- install.sh and uninstall.sh (per user, no sudo).

Skipped tests and why:
- SCN-01, INT-02, INT-05, SEC-01 live, SEC-08 live, SEC-09 live, SEC-16 live, SEC-21 live: need Docker, the openmono-agent image, the relay or a second LAN host.
- REG-01: OMA's own suite runs in OMA CI; REG-02 proves no OMA file changed.

## Phase 2: roster gateway, capacity and secrets

Done:
- LaneQueue per backend: S lanes, four priorities, round robin by bot, per bot cap of 2, background drop rule (B23), sticky lanes, cancel, live S changes.
- Slot discovery from Strata `/v1/status` and llama-server `/slots`, measured override order, refresh loop and unknown slots notice (B21).
- Label routing over backends with per backend model names; implicit main backend from `gateway.upstream` (B22).
- Usage metering per bot and routine (roster.db `usage`), `oma-bot usage`; streamed bytes pass through unchanged (B24).
- Turn context table so the roster, not the bot, sets priority; `oma-bot run` marks turns interactive.
- `oma-bot gateway status | slots | bench [--save]`; bench report in `~/.omabot/reports/`.
- `oma-bot secrets set | list | rm | rotate-bot`, per bot folders, team file secret materializing, startup secrets guard, redactor loaded with every stored value. See docs/secrets.md and docs/gateway.md.

Tests added: UNIT-GW-01, 02, 03, 05, INT-07 (stub server), LOAD-01 (simulated), SEC-05 (unit), SEC-08 (spec), SEC-12 exit check, secrets CLI, guard, bench.

Skipped and why:
- LOAD-01 live, INT-07 live, INT-13, LOAD-06: need the real inference server through the FRPS and FRPC relay and the main box.

## Phase 3: per-bot memory (M3)

- Core: Memory/MemoryEntries, MemoryBudget, MemoryStore (revision checks, redaction, budget, atomic writes, journal, undo, symlink refusal, direct write scan), Recall, MemoryUpkeep (capture, organize, tidy), MemoryTools.
- Roster tools host and per-bot shim sockets (Api/RosterTools, Api/ShimServer) with per-turn tokens; the stdio MCP shim in bot/mcp-shim.
- `oma-bot run` refreshes OPENMONO.md with memory, adds recall notes, logs the turn and records it for capture. `oma-bot serve` runs the shim sockets and upkeep at background priority through the gateway.
- Tests: UNIT-MEM-01 to 09 plus revision, budget, limits, symlink, direct write, forget, recall, capture, tidy cap and shim round trip.
- Skipped: SCN-02 (Docker, live gateway), INT-04 (Docker).
- Decisions B25 to B30 need Spencer review.

## Phase 4: routine scheduler (M4)

Done:
- Routine rules (Routines/RoutineRules): cron with its own IANA zone (L, W, #), weekdays, monthly and yearly presets stored as cron, intervals counted from the last fire, one-time runs, watch triggers on a path, the inbox, doctor results or a check script. DST gaps move forward, repeated hours run once.
- Checks (Routines/Checks): file_changed, git_new_commits (refs read from disk, git never runs on the host), http_status (allowlist, resolve once, every address public, connect to the checked address), command (throwaway container through the supervisor: no network, read-only root and workspace, caps dropped, time limit, always removed), doctor, assignments_waiting, always.
- Scheduler (Routines/RoutineScheduler, RoutineStore): quiet runs make no model call; one log line and one row per run; notify only on error, failed or needs_input; overlap skip, daily token budget with one notice per day, 12 hour catch-up with missed rows, fixed jitter, routine share of slots, max run time; a unique run key per occurrence so nothing fires twice; crash recovery marks cut runs interrupted once and releases their lease.
- Runs (Routines/RoutineRunner, Runtime/TurnDriver, TurnGate, Envelope): one roster turn per bot (turn lease with an owner), fresh session in build mode, harness-written envelope with marker escaping, a turn gate the bash hook enforces (changing tools blocked in routine turns on edits and auto bots).
- Approvals store (Approvals/ApprovalStore) with append-only events, action hash binding, expiry and single use; routine changes from bots become cards (Routines/RoutineChanges, RoutineTools); `oma-bot approvals`.
- CLI: `oma-bot routines list|runs|next|run-now|pause|resume`, `oma-bot approvals`; `oma-bot serve` runs the scheduler. `oma-bot run` now waits for a busy bot. Docs: docs/routines.md.
- Fix found while building: the shim served every roster tool not on a bot's deny list; it now serves only the bot's allow list (B40).

Tests added: UNIT-RT-01 to 08, SCN-03 (simulated five weekdays with a fake ACP server), SOAK-01 (simulated 72 hours, 4 bots, 12 routines, two zones), restart without double fire, DST across a clock change, slot share, budget, watch retry, run-now, pause, one-time, jitter, every check kind, routine tools and approval binding, team file refusals and presets, turn gate hook, turn leases, shim allow list, check container safety.

Skipped and why:
- SCN-03 live and SOAK-01 live: need Docker, the openmono-agent image and the inference server through the relay over real days.
- Command check in a real container: needs Docker and the agent image.

Decisions B31 to B44 need Spencer review.

## Phase 5: handoff runtime (M5)

Done:
- Tables: assignments, assignment_events (append-only), inbox (one report and at most one wake per result), outward_actions; usage rows and turn records carry the assignment.
- Runtime (Runtime/AssignmentRuntime, AssignmentStore): typed enqueue checks (ancestor, depth, token, turn and time budgets, fan-out, tree size, dedupe, same-request join, level, accepts_from); admission on a free bot and an open gateway lane; work, consult (plan mode, gated) and notify (gated, no reply); settle once, report once, wake once; questions to Spencer or to the requester; cancel cascades and aborts; deadlines fail the tree; stall guards on continuations; restart reconciliation (wait for children, never repeat an outward action, rerun once, fail when cut twice).
- Routing by name, team lead or keyword routes (Runtime/Routing). Harness-written envelopes for work, consult, notice and report.
- Tools (Runtime/HandoffTools): assign, consult, notify, assignment_status, ask, answer, cancel, report. The caller and its assignment come from the roster's turn record.
- A person's `oma-bot run` message and each routine run are root assignments (Routines/AssignmentRoutineRunner). Idle sleep and wake (Supervision/IdleSleeper). The pre tool hook marks outward tools before they run.
- Invariant checker for I1 to I10 (Runtime/Invariants), run after every scenario test.
- CLI: `oma-bot assignments`, `oma-bot audit tree`, `oma-bot run --mode --wait-done`, `oma-bot approvals --answer ID allow --text`. Docs: docs/handoffs.md.
- Found while building: a child's question to its requester was closed when the parent's resume turn started, so it could never be answered; questions now stay open until answered and lead the resume turn.

Tests added: UNIT-HO-01 to 08, UNIT-INV-01 to 10, SEC-04, SCN-04 (three-bot chain with audit replay), SCN-05 (ancestor and depth), SCN-09 (sleep and wake), SCN-10 (consult cannot write), crash mid-chain (M5), restart cases, same request, questions both ways, notify, deadline and cancel, routine roots, `oma-bot run` as a root, typed refusals, routing.

Skipped and why:
- SCN-04 live and SCN-09 live: need Docker, the openmono-agent image with the MCP shim, and the inference server through the relay.

Decisions B45 to B53 need Spencer review.

## Phase 6: approvals and the permission broker (M6)

Done:
- Approval broker (Approvals/ApprovalBroker): every pause kind (permission, playbook, toggle_mode, user_input, host_exec, plan) becomes a card; the safety floor (Approvals/SafetyFloor) and hard deny rules answer deny at once, team file auto rules (Approvals/AutoRules) answer allow at once, everything else waits for Spencer or a bound approver bot while the pause stays open in the bot. Decided cards are delivered by the service tick and the rest of the turn is read as usual (Runtime/AssignmentRuntime: AnswerPauseAsync, ContinueParked).
- Pause timeout with a pause_expired follow-up question; reminders after 4 hours and cancel after 24 for questions to Spencer; answers bound to the action hash, single use, expiring.
- No self-approval (Approvals/ApproverPolicy): person roles and user binding; bot approvers only for bound cards of listed kinds, never the requester, an ancestor or descendant, a controlled, managed or edited bot, head-honcho or the health bot. `approval_answer` tool for approver bots; approvers in roster.json and the team file.
- A pause answered outside the roster is caught when the bot refuses the broker's answer: turn ended, bot disabled and stopped, work failed, alarm notice.
- Bash hook rules for loopback names, the host bridge variables and the process environment; `bash.allow` and auto rules refuse network clients and interpreters (threat T3).
- agent-host invite minting for host_exec bots (HostExec/HostBridgeClient): pair secret read from agent-host's 0600 config on loopback only, invite and address passed to the container, fresh container on every start, idle sleep before the invite runs out.
- head-honcho bot tools (Runtime/BotTools, BotChanges): bot_update, bot_create, bot_pause, bot_resume, only in person-started root work, version checked, always person-only cards except pause. `bot_versions` table (migration 6) with `oma-bot bot history|undo`.
- CLI: `oma-bot approvals` shows the action and binding, `--answer` applies bot changes, `oma-bot run` prints the card to answer; `oma-bot serve` runs the broker. Docs: docs/approvals.md.

Tests added: UNIT-AP-01 to 05, SCN-06, SCN-07, SCN-08, SCN-11, SEC-03, SEC-23, SEC-26, SEC-27, SEC-28, SEC-29, SEC-30, SEC-31, HEALTH-02, safety floor cases, auto rule matching (operators, dot segments, symlinks), team approvers and auto rules, delivery once, AskUser text answers, auto allow, plan and toggle answers, approver bot flow, approver skipping controlled bots, routine host exec, stuck work, bot management with undo, stale bot cards, host invite minting, invites in containers, launcher minting and renewal.

Skipped and why:
- INT-06 (HostExec through agent-host with a short TTL invite): needs Docker, the agent image and a running agent-host on the host.

Decisions B54 to B70 need Spencer review.

## Phase 7: desktops and the egress gate (M7)

Done:
- Roster networks (Egress/Networks): internal bots and desktop networks and the egress network with fixed subnets and named bridges; the supervisor creates or verifies them and refuses a mismatch.
- Firewall rules (Egress/FirewallRules): OMABOT-USER and OMABOT-INPUT rendered as a script (`oma-bot egress rules`), verified against `iptables-save` before every networked container start (fail closed), `oma-bot egress check`.
- The egress gate (Egress/EgressProxy, EgressPolicy, Omabot.EgressGate): per-client tokens, ports 80 and 443, allowlists and blocked hosts, its own lookup with every address checked, connection only to the checked address, a log line per decision; policy written by the service (Egress/EgressClients); refusal alarm (EgressMonitor); gate container spec and image recipe.
- Bot containers carry their own gate client in HTTPS_PROXY with the gateway in NO_PROXY; networked command checks use the check client.
- Gateway search and scrape for OMA's web tools (`/v1/services`, `/v1/search`, `/v1/scrape`) with the bot's allowlist enforced on scrape URLs.
- Desktops (Desktops/DesktopManager, DesktopService): own, shared and pool modes, cap 8, leases ending with their work or time, idle stop, home folders kept, view-only noVNC passwords, service container spec checked by the supervisor, `oma-bot desktops`; the shim's desktop mode bridges two fixed tools to the desktop MCP server; desktop image recipe pinned by required build arguments.
- Docs: docs/egress.md, docs/desktops.md.

Tests added: UNIT-EG-01, UNIT-EG-02, UNIT-EG-03, SEC-06 (gate for agents and desktops, and the fail-closed supervisor), SEC-07, SEC-11, gate rules (credentials, ports, request shape, limits), policy writer, refusal alarm, firewall rules, network config and supervisor networks, service and desktop specs, desktop leases and idle stop, desktop cap, desktop rendering and hook patterns, gateway search and scrape, desktop bridge.

Skipped and why:
- SCN-13 (desktop browse watched on noVNC): needs Docker, the pinned desktop image with cua-driver, the gate image and a public site.

Decisions B71 to B80 need Spencer review. Open items: the desktop base digest, cua-driver version and MCP command; applying the firewall script and the sudo rule on the host.

## Phase 8: local model hardening (M8)

Done:
- Tool-call repair in the gateway (Gateway/ToolCallRepair): argument repair without guessing, text call recovery (Hermes, Qwen, Qwen coder, single JSON) only at the end of the reply and only for offered tools, never from reasoning, type coercion to the schema, at most 32 calls; JSON and SSE replies assembled and rebuilt.
- One resend for unreadable calls, then plain text; never dropped silently.
- Request shaping (Gateway/RequestShaper): tool count limit (ceiling 128), output cap at a quarter of the context, spill of long tool results to a 0600 file in the bot's data folder, repeat-streak note.
- Gateway event log and counts in `oma-bot gateway status`.
- Sampling presets per bot (`bots.presets`, team file `preset:`), rendered safely for OMA's preset merge.
- The 40-task tool set with a scorer and `oma-bot gateway toolbench`. Docs: docs/local-models.md.
- Found while building: a usage-only stream chunk with an empty choices array would have crashed the parser.

Tests added: UNIT-TC-01 to UNIT-TC-07, the 32-call cap, SSE and JSON round trips, gateway repair of streamed and text calls, the one resend and the text fallback, pass-through without tools or with repair off, request shaping, presets, the task set against a scripted model at 1, 2 and 4 parallel requests.

Skipped and why:
- LOAD-04 live: needs the inference server through the relay (`oma-bot gateway toolbench --save` on the main box).

Decisions B81 to B91 need Spencer review. Open item: the agreed success target after the first measurement.

## Phase 9: rooms, polish and load (M9)

Done:
- Rooms (Rooms/RoomService): messages in roster.db (append-only), the mentions, lead and everyone responder rules, each member turn a root assignment on the bot's room session with the room fenced as data, chained mentions up to 3 hops at the ask level, at most 6 bot posts per message from Spencer, a 10 minute turn, one turn at a time per room, `max_posts_per_hour`. Tools room_post and room_read for members; `oma-bot room list|show|post`.
- The local web view (option U1, plain HTML and one ES module, embedded in Omabot.Core): bots, approvals with answer buttons bound to the action hash, routines, rooms, memory with undo, audit (owner only). Strict CSP with no inline script and the usual hardening headers, Host allowlist, Origin and CSRF checks on every change, read-only viewers, one-time sign-in links from `oma-bot web open`, sessions in users.db with server-side expiry, sign-in lockout per address. Local mode binds loopback only.
- Load and soak cases in simulated time: four and eight bots through the gateway at measured slots with fairness and wait targets; an inference restart in a busy hour; a bot killed every 2 simulated hours for 72 hours; the health bot quiet while healthy and one report per new problem.
- Found while building: a bot the health monitor was restarting failed its queued work instead of keeping it (fixed, SOAK-02).
- Docs: install.md, team-file.md, cli.md, security.md, capacity.md, rooms.md, web.md, release-checklist.md. The docs tests check that the CLI reference covers every command, that no secret appears in docs, templates or the README, and that the team file example applies; REG-06 runs the dash check.

Tests added: SCN-12, room limits and hops, mentions, room tools, web hardening (headers, Host check, sign-in links, CSRF and Origin and the action hash, viewers and sign out, escaping, local mode), LOAD-02, LOAD-03, SOAK-02, SOAK-03, HEALTH-03, HEALTH-04, and the three docs tests.

Skipped and why:
- LOAD-05 (agent box resources at 8 bots and 2 desktops stay under 80 percent): needs a Docker host with the agent image and the desktop image; measure with `oma-bot status` and `docker stats` on the roster host.
- SCN-15 (FRPC stopped on the inference box, every bot sees inference unreachable within a minute and work resumes with the tunnel): needs the FRPS and FRPC relay and the inference box. The gateway half is covered by SOAK-03 (simulated) and the health report by HEALTH-03.
- INT-14 live (the health bot reports relay down, container down, disk low and queue age with zero model calls when healthy): needs Docker, the agent image and the relay; the routine logic is covered by HEALTH-03 and HEALTH-04.
- SOAK-02 and SOAK-03 live (72 hours with real container kills and a real inference server restart): need a Docker host running the roster and the inference server through the relay; both run here in simulated time.

Decisions B92 to B98 need Spencer review.

## Phase 10: web instance, single-owner (M10)

Done:
- Web-single mode: the roster stays on 127.0.0.1 and the HTTPS front (docker/web-front, Caddy pinned by digest) terminates TLS. Every request must say it came over https through the front; the client address comes from the front.
- `oma-bot web enable` refuses without an https public origin with a DNS name, the front setting, a loopback API, the password and code login, a strong 0600 admin secret and an active owner, and proves the public address has a valid certificate and reaches this roster. `oma-bot serve` refuses to start in web mode on the same problems and keeps sign-in closed until the front check passes. `web disable`, `web status`, `web admin-secret [--rotate]`.
- Accounts in users.db: PBKDF2 passwords, time-based codes with seeds sealed under the admin secret, roles owner, operator and viewer, pending until confirmed on the host. `oma-bot users add|confirm|list|role|disable|enable|logout|password`, passwords and codes on stdin only.
- Sign-in with account and address lockout and doubling backoff, one message for every refusal, audited failures, single-use codes. Sessions with a new id at sign-in, idle and absolute expiry, `__Host-` Secure cookies, sign out everywhere.
- The page: a sign-in form in web modes, sign out everywhere, and a source link (AGPL-3.0 section 13) in every mode.
- Doctor: web checks in every mode, and the front check with `--online`.

Tests added: SEC-15, SEC-17 (the refusals, serve and doctor, and `web enable` against a TLS test front with a trusted CA, a rogue CA, an unreachable front and a front that drops the https mark), SEC-18 (lockout, backoff, address lockout, audit, codes), SEC-19 (fixation, idle and absolute expiry, sign out everywhere, role change and disable, the front gate), SEC-23 for the web, and unit tests for codes, passwords, the users commands, admin secret rotation, the web settings and the front recipe.

Skipped and why: none new. The live front (Caddy getting a public certificate) needs a public host name and Docker; the certificate and forwarding checks run against a TLS test front here.

Decisions B99 to B111 need Spencer review. Open item: passkeys (plan 7.20 prefers them).

## Phase 11: multi-user mode and the security release gate (M11)

Done:
- Web-multi user rosters (Web/Tenants): `oma-bot users add NAME --role owner` makes a whole roster under `users/NAME` with an isolated roster.json (container prefix, networks, subnets, bridges, firewall chains, port block, workspace root); operators and viewers join one with `--home`; `oma-bot users homes`. Per-user supervisor and service through systemd templates written by `install.sh --systemd`.
- The supervisor in a user roster prefixes and labels container names on the engine, keeps the plain names as aliases on the user's own networks, and lists, stops and replaces only its own containers. Firewall chains are per user. Host commands are refused in user rosters.
- The web view in web-multi opens only the signed-in account's roster for every API call. Web-multi starts only with `web/release-signoff.md` recording the passed gate and the outside review.
- SEC-14: no bot starts until a person accepts the agent image (`oma-bot images [status] | accept`), and a changed image is refused again; doctor checks it.
- SEC-25: `oma-bot release check` (dash check, secret scan, package pins, critical advisories from a file or `dotnet list package --vulnerable`, image digests, Spencer's sign-off). The online advisory check runs from this build box.
- SEC-33: refused roster tool calls are audited; `oma-bot`, `iptables` and `nft` can never start a bot's bash rule.
- Docs: docs/multi-user.md, docs/signoff.md (for Spencer), and updates to the release checklist, security notes, install guide and CLI reference.

Tests added: SEC-14, SEC-20 (two users through the real web server), SEC-21 and SEC-22 for two users (configs, firewall, tokens, supervisors on one engine), SEC-25, SEC-27 to SEC-31 in two user rosters, SEC-33, and the web-multi users commands.

Skipped and why:
- SEC-32 (the inference key on the relay leg): checked against the fix Spencer chooses in task V4 (decision D20), and needs the FRPS and FRPC relay.

Not for the builder: the outside review of web-multi and Spencer's sign-off (docs/signoff.md and, for web-multi, web/release-signoff.md on the host).

Decisions B112 to B121 need Spencer review.

## Phase 12: fleet mode, optional (M12)

Done (off unless Spencer turns it on by name):
- The fleet controller (Fleet/FleetController, Fleet/FleetServer): a private CA, one-time invites bound to a node name, enrollment where the node keeps its key and checks the CA fingerprint first, approval of the key by a person, mutual TLS with each node on its own relay port, drain, revoke and pause.
- Jobs from the CLI only: read-only kinds under a standing policy, everything else on a person-only card bound to the job hash, approved_by written from the record. Pull with leases and fencing tokens, an append-only intent and done ledger for steps, lost jobs that wait for a person, artifacts with digest checks, per-job secrets as 0600 files.
- The node agent (`oma-bot-node enroll | run | ledger`, `install.sh --node`): read-only jobs through the node's own OMA agent, its gateway token over mutual TLS only.
- `oma-bot fleet ...` commands; the controller runs in `oma-bot serve` only when fleet mode is on.
- Docs: docs/fleet.md, docs/fleet-inventory.md (F0, for Spencer).

Tests added: the fleet guard and CLI, FLEET-01 to FLEET-08 (including four chaos cases with zero repeated outward actions).

Skipped and why:
- FLEET live (two enrolled nodes over the relay): needs two nodes, the FRPS and FRPC relay with one remote port per node, and Spencer's direction.
- F7 rollout with canary and rollback: not built; needs Spencer's release key and real nodes.
- F9 scale and 7 day soak: needs the fleet hardware.

Decisions B122 to B135 need Spencer review.

## Where things stand

All phases (0 to 12) have their code, tests and docs. 358 tests pass and 31 are skipped, each naming what it needs (Docker, the agent or desktop image, the relay, the inference server, a public host name, fleet nodes, or Spencer's decisions D20 and F0). Open items for Spencer: the builder decisions in docs/decisions.md, the desktop base digest and cua-driver version, applying the firewall script and sudo rule on the host, the tool-call success target after measurement, passkeys for web sign-in, decision D20 for the relay leg (SEC-32), the outside review before web-multi, the release sign-off (docs/signoff.md) and the fleet inventory (docs/fleet-inventory.md).
