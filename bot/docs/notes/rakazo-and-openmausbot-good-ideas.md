# Rakazo and OpenMausBot: good ideas for the OMA bot roster

Read-only review written Nov 3, 2026 (America/Phoenix, MST). Nothing was edited, pushed or sent. Sources: a shallow clone of elie222/rakazo at /workspace/rakazo-ro (head f20d413, Nov 3, 2026 5:52 PM MST) and the existing clone of milind-soni/OpenMausBot at /workspace/openmausbot (head 6371611, Nov 2, 2026 6:29 AM MST). Targets: OMA (AGPL-3.0), plan at OMA-BOT-ROSTER-PLAN.md, branch review at oma-bot-branch-review.md.

## License summary

- Rakazo is Apache 2.0 (LICENSE). It has no NOTICE file today. Copying code into OMA is allowed: keep the Apache license text, mark changed files, and credit Rakazo in our NOTICE. Apache 2.0 code may go into an AGPL-3.0 project, but not the other way.
- OpenMausBot is Apache 2.0 except `enterprise/`, which is source-available and must not be copied (LICENSING.md). Its NOTICE lists MIT pieces it ported (T3 Code, Cache Tax, Hermes Agent skill review in server/skill-review.ts). If we copy one of those files we must also carry that upstream MIT notice.
- Copying ideas and designs needs no license. Copying code needs attribution in a NOTICE file in the new roster folder.

## Part 1. Rakazo

**What it is.** An open source, self-hosted "persistent AI teammates" app, described as an open alternative to Grok Bot, OpenClaw and Hermes Agent. In beta.

**Language and stack.** TypeScript monorepo (pnpm, turbo). React 19, Vite and Tailwind web app; Electron desktop; Expo mobile; Hono and oRPC API; PostgreSQL with Prisma and row-level security; Graphile Worker for jobs; Pi (an open agent loop) as the agent runtime.

**Architecture.** apps/api, apps/worker, apps/web, apps/desktop, apps/mobile, plus packages (core, adapters, adapter-kit, memory, contracts, db). The agent loop runs inside the API or worker process, and the "computer" (shell, files, browser, desktop) is a separate sandbox reached through a SandboxProvider interface (docs/computer-runtime.md). Vendors live only behind adapters (AGENTS.md).

**Agents.** One bot per job, each with one continuous chat, instructions, memory, model, and a Team or Private computer (docs/bots-and-chats.md). Group chats of 2 to 10 bots with @mention wake rules. Bots message peers directly and choose whom to ask from a one-line "How other bots see it" description. Short-lived subagents run in parallel and cannot nest (packages/adapters/src/pi-runtime-subagent.ts line 52). "Bots that manage bots" tools such as list_fleet, set_child_instructions and create_child_routine, gated by who started the run (docs/fleet-management.md).

**Memory.** Per-bot MEMORY.md, one fact per line, capped at 4 KB, rewrite instead of append (docs/memory.md). Shared memory per space. Revisioned documents with conflict retry (packages/memory/src/index.ts lines 76 to 96). A per-bot "Open work" list. Optional cloud memory providers.

**Routines.** Schedule (cron with user time zone, packages/core/src/cron.ts), "when something changes" watches, webhooks, git events and Slack messages (docs/routines.md). The watch checker never calls a model; it fingerprints files, a command's output or a tool result and runs only on change (packages/adapters/src/routine-watch.ts lines 35 to 38). Per-routine model, test run, pause, run history.

**Tools.** Built-in shell, file, browser and desktop tools as plain agent tools (packages/adapters/src/builtin-tools.ts), MCP and OpenAPI sources, Composio or Pipedream catalogs, a credential vault where the bot only references a secret by name and the backend injects it (docs/bot-secrets.md).

**Inference.** Cloud by default (OpenRouter, Anthropic), but first-class local models: RAKAZO_LOCAL_MODELS and RAKAZO_LOCAL_MODELS_URL for Ollama, llama.cpp, LM Studio or MLX, with context window, max tokens capped at a quarter of context, vision and reasoning flags (docs/self-host-models.md). Main model plus ordered fallbacks.

**Sandboxing.** Docker computers with memory and CPU ceilings, a supervisor that alone holds the Docker socket behind a token, and a restricted egress mode that drops traffic to the host, LAN and metadata via iptables on named bridges, and refuses to start a computer if the rules are missing (docs/self-host-docker-computers.md, infra/compose/restrict-computer-egress.sh). Per-bot Chrome profile and X display.

**Approvals.** Ask, Auto and Never ask modes; Allow and Block lists; "always asks" cases that cannot be skipped, such as instruction changes and anything steered by outside text (docs/approvals.md). Auto mode uses a hosted approval model by default.

**UI.** Web, Electron and mobile, with cards for questions, approvals and takeovers that never block the chat; optional tool activity cards.

**Audit.** Append-only audit events with no message content or secret values; Postgres rejects updates (docs/self-host-audit-log.md).

**Activity.** Very active. Created Aug 13, 2026; about 3,249 PRs by number; pushed today; about 6,860 stars, 1,212 forks, 94 open issues (GitHub API).

### Rakazo good ideas for OMA

1. **Change-watch routines with no model call.** routine-watch.ts fingerprints the watched thing and only starts a run on change; a busy computer retries in 60 seconds and is not a failure. Maps to our routine "check first" rule; adds file and command watches as a trigger type. Phase 4 (routines). Effort small to medium. Apache 2.0, copy with attribution. No cloud dependency.
2. **Restricted egress that fails closed.** iptables DOCKER-USER rules keyed on bridge names, plus a startup check that refuses to run an unrestricted computer (restrict-computer-egress.sh, docs/self-host-docker-computers.md). Maps directly to OMA's per-bot agent containers. Phase 7 (desktops) and should move earlier to phase 2 for the agent containers themselves. Effort medium. Apache 2.0, the script can be adapted with attribution. No cloud dependency.
3. **Supervisor owns the Docker socket.** Only one internal service holds the socket and needs a token; bots never see it. Maps to the roster service that starts bot containers, and closes the self-approval risk in our plan. Phase 2 (launcher). Effort small, design only. No cloud dependency.
4. **Local model settings shape.** Context window, max tokens capped at a quarter of context, vision list, reasoning list per endpoint (docs/self-host-models.md). Maps to per-bot model nicknames on llm.endpoint and to the gateway. Phase 2 and phase 8. Effort small. Design only. Drop the OpenRouter default.
5. **Fallback models.** An ordered backup list tried only when the main model fails. Maps to the gateway: a second local model or a second llama-server. Phase 8. Effort small. Keep local only.
6. **"Always asks" list.** Instruction changes, model changes and any action steered by outside text always show a card, even when a tool is always allowed (docs/approvals.md, docs/fleet-management.md). Maps to OMA PermissionEngine plus roster approvals. Phase 6 (approvals). Effort small. Drop the hosted TypeSafe or OpenRouter auto-reviewer; if Auto is wanted, use a local model and treat "unsure" as ask.
7. **Fleet tools gated by run origin.** Bot management tools work only in runs a person started, never in routine, webhook or peer runs; instruction edits carry the version read (docs/fleet-management.md). Maps directly to the head-honcho lead bot. Phase 5 (handoffs) and phase 6. Effort medium. No cloud dependency.
8. **"How other bots see it" one-liner.** A short description per bot used for routing. Maps to team YAML. Phase 2. Effort small.
9. **Instruction history with undo.** Every instruction change is versioned and restorable (docs/bots-and-chats.md, packages/core/src/instruction-diff.ts). Maps to our change journal, applied to OPENMONO.md and prompt files. Phase 3. Effort small.
10. **Revisioned memory writes.** A write must name the revision it read, or it conflicts and retries (packages/memory/src/index.ts). Prevents a person's edit and a bot's write from clobbering each other. Phase 3. Effort small.
11. **Stuck work reminders.** Remind after 4 hours waiting, cancel after 24 hours, takeover waits after 30 minutes (packages/core/src/stuck-work.ts). Maps to OMA pause types, which time out at 10 minutes today. Phase 6. Effort small. Apache 2.0.
12. **Secret references, not values.** request_secret and secret_request inject a stored credential by name for one HTTPS origin, with redacted responses (docs/bot-secrets.md). Maps to the gateway or an MCP tool on the roster side. Phase 5 or later. Effort medium. No cloud dependency.
13. **Startup secrets guard.** Refuse to start with placeholder secrets outside development (packages/core/src/secrets-guard.ts). Maps to the head-honcho health check. Phase 2. Effort small.
14. **Append-only audit events without content.** Actor, action, target, outcome, never message text or tool arguments (docs/self-host-audit-log.md). Maps to our SQLite audit table; add a trigger that rejects UPDATE. Phase 5. Effort small.
15. **Import an agent.** Reads OpenClaw or Hermes folders, maps SOUL.md, MEMORY.md, skills and jobs, and starts anything that runs code unticked and routines paused (docs/import-an-agent.md, packages/core/src/agent-import/). Maps to a later `roster import` command. Phase 9. Effort medium.
16. **Test layers.** Scripted model for fast tests, loopback model endpoint for protocol tests, real model only on explicit command; missing credentials mean "not run" (docs/agent-verification.md). Maps to our test plan against a fake llama-server. Phase 1 onward. Effort small.

## Part 2. OpenMausBot re-review (items the plan missed)

The plan already adapts the runtime record, memory budget and redaction, routines, egress gate address policy, per-turn capability token, team apply and text tool-call recovery. The items below are new or only touched lightly.

1. **Restart policy that never repeats an outward action.** On boot, a running turn that already sent, posted, pushed or paid is marked failed(interrupted) instead of rerun; otherwise it reruns once (docs/runtime-contract.md section 5). Maps to the handoff store and to routines that post. Phase 5. Effort medium. Apache 2.0.
2. **The ten invariants as a test suite.** I1 to I10, for example no result delivered twice, expired is never completed, the runtime never throws into a turn (docs/runtime-contract.md section 7, server/runtime/invariants.ts). Phase 5 test plan. Effort small. Copy allowed with attribution.
3. **Harness-written envelope.** The opening of every delegated turn is written from stored data, says outside text is data and not instructions, and escapes model text that imitates the marker (section 4, server/runtime/envelope.ts). Phase 5. Effort small.
4. **One answer to the same request.** Identical requests to the same bot (40 characters or more, same mode) wait for one answer (server/same-request.ts). Saves inference slots. Phase 5. Effort small.
5. **Stall guards.** Two continuations without a successful tool call, or three identical errors, end the work as stalled (section 6). Complements OMA's doom loop tiers. Phase 8. Effort small.
6. **Turn watchdog that ignores waits.** Silence on a thread means wedged, but waiting on a person or computer is not a stall, and a running tool gets a longer window (server/turn-watchdog.ts). Phase 2 supervisor. Effort small.
7. **Continue only on a spent step budget.** Provider errors and denied tools are never auto-retried (server/turn-continuation.ts). Phase 4 and 5. Effort small.
8. **Recall placed in the message, not the system prompt.** Up to four notes and four passages, 6,000 characters, labeled as notes not instructions; kept out of the system prompt so the cached prefix stays stable (docs/memory.md "Recall before each turn"). Important for llama-server prompt cache reuse. Phase 3. Effort medium.
9. **Recent-work brief and per-turn log line.** A ten-line block of what the bot said in its other conversations over two days, plus one log line per finished turn (docs/memory.md). Makes standup routines work without transcripts. Phase 3 and 4. Effort small.
10. **Archive instead of overflow.** When MEMORY.md would pass budget, the oldest dated entries move to memory/archive.md, archive written first; entries capped at 1,000 characters and 20 lines; "until" dates expire notes (docs/memory.md). Phase 3. Effort small.
11. **Revision-checked team memory.** Stale writes return 409, missing revision 428 (docs/memory.md "Shared team memory integrity"). Phase 3. Effort small.
12. **Bot box self-check with mutations.** A probe counts what a hostile bot can and cannot do, and removing one protection must fail exactly its checks (docs/bot-box.md, server/bot-box/self-check.ts). Maps to a head-honcho check that our containers really block LAN, the ACP port and the Docker socket. Phase 2 and 7. Effort medium.
13. **Environment allow-list.** Boxed processes get only listed variables (server/bot-box/env.ts). Maps to the docker run env for each bot. Phase 2. Effort small.
14. **Credential broker.** Real keys stay outside; the bot gets a per-process token and a local base URL; wrong token is 401 (docs/bot-box.md, server/bot-box/broker.ts). This is the same idea as our gateway; adopt its checks. Phase 2. Effort small. Drop the cloud provider list.
15. **Safety floor.** A never-approve list of shell commands (delete root or home, format disk, fork bomb) at every level; a line not fully read is "could not read", never "clear" (server/safety-floor.ts). Maps beside OMA PermissionEngine and HostExec. Phase 6. Effort medium.
16. **Command allowlist validation.** Grants are matched without resolving symlinks or folding `..` (server/command-allowlist.ts). Same class of bug as the branch's workspace path check. Phase 2 and 6. Effort small.
17. **Tool count limit.** Many OpenAI-compatible servers refuse more than 128 tools; each MCP server lists its share (server/tool-count-limit.ts). Phase 8. Effort small.
18. **Tool result spill.** Long tool results are cut and the rest saved to a private 0600 file (server/tool-result-spill.ts). Saves context on local models. Phase 8. Effort small.
19. **Warm summary.** Compact a long chat while it is idle so the next turn starts small (server/warm-summary.ts, server/compaction-summary.ts keeps the last two exchanges). Maps to OMA's Compactor; run it from a roster routine at background priority. Phase 8. Effort medium.
20. **Usage ledger without content.** Append-only 0600 rows per turn, no message text (server/usage-ledger.ts). Local only, not telemetry. Phase 9. Effort small.
21. **Encrypted workspace backup.** Quiesce writers, snapshot, restore before config loads (server/workspace-backup.ts). Phase 9. Effort large.
22. **Background skill review.** After replies, a bounded review proposes skill updates (server/skill-review.ts). Note this file is ported from Hermes Agent under MIT, so copying it carries that notice too. Later phase. Effort medium.

## Not worth adopting, and why

- Rakazo Postgres, Prisma, row-level security and Graphile Worker: too heavy for a loopback roster; SQLite fits OMA.
- Rakazo running the agent loop in the API process: OMA keeps the agent in Docker, which is safer and already works.
- Rakazo Team Computer shared folders: its own docs say they are not security boundaries; we want one container per bot.
- Hosted auto-review (TypeSafe Jev, OpenRouter), Composio, Pipedream, Treg, E2B, Daytona, Boat, Supermemory, Honcho, ElevenLabs and other cloud voice: cloud or frontier dependencies.
- Rakazo spaces, organizations, billing and credits: multi-tenant features one owner does not need.
- Electron, Expo and the React UI as code: large; a small local web view later is enough.
- OpenMausBot bubblewrap bot box: OMA already isolates with Docker; take the self-check and env rules, not bwrap.
- OpenMausBot cache keep-warm: only helps paid subscription prompt caches.
- OpenMausBot enterprise/, cloud lending, Team Cloud, MausBot Cloud: license or cloud.

## Recommended top 10, ranked

1. Fail-closed restricted egress for every bot container (Rakazo). Phase 2.
2. Restart policy that never repeats an outward action, plus the ten invariants as tests (OpenMausBot). Phase 5.
3. Supervisor alone holds the Docker socket and a credential broker style gateway with its self-checks (both). Phase 2.
4. Change-watch routines with no model call (Rakazo). Phase 4.
5. Harness-written envelope and "always asks" list for outside-steered actions (both). Phases 5 and 6.
6. Recall in the message, archive on overflow, revisioned memory writes (both). Phase 3.
7. Stall guards and a wait-aware turn watchdog (OpenMausBot). Phases 2 and 8.
8. Fleet tools for head-honcho gated by run origin, with versioned instruction edits and undo (Rakazo). Phases 5 and 6.
9. Safety floor and symlink-safe allowlist matching (OpenMausBot). Phase 6.
10. Local model settings, fallbacks, tool count limit and result spill in the gateway (both). Phase 8.
