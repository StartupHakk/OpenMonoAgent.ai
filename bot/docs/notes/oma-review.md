# OMA read-only review for the bot roster plan

Repo: StartupHakk/OpenMonoAgent.ai, main at e4b8eae (Oct 29 2026, 13:57 Phoenix time), VERSION 1.9.0, .NET 10.
Clone at /workspace/oma-readonly. Nothing was edited. Only fetches were run (including an unshallow fetch).
Spencer's setup assumed: agent in Docker, agent-host (src/OpenMono.HostBridge) on bare metal, Qwen3.8-Flash-Next on Strata on a separate inference server (Mode B).

## 1. How the agent works

Startup: src/OpenMono.Cli/Program.cs RunAgentAsync (lines 115 to 372). It loads config (ConfigLoader), probes /props for the model, builds MemoryStore(DataDirectory), HookRunner and ProviderRegistry.CreateClient, registers about 21 built in tools plus MCP tools, and starts ACP when --acp-only or OPENMONO_ACP_ENABLED=1. A DI container (lines 299 to 325) holds singletons for config, the LLM client, ToolRegistry, AcpSessionStore, MemoryStore, ConversationLoopFactory and AcpTurnRunnerFactory.

Docker sandbox: docker/docker-compose.yml service "agent" (about line 100). Mounts ${WORKSPACE} at /workspace and ${HOME}/.openmono at /home/agent/.openmono. host-bridge.json is shadowed by /dev/null and the log dirs by tmpfs, so the sandbox cannot read the pair secret or tamper with audit. docker.sock is opt in (OPENMONO_DOCKER_SOCK=1). container_name is fixed as openmono-agent.

ConversationLoop (src/OpenMono.Cli/Session/ConversationLoop.cs): constructor lines 52 to 130 wires Compactor (80% of context), Checkpointer (65%), TurnJournal, ToolResultCache, ArtifactStore, LocalToolExecutor. Tools offered per mode (lines 469 to 482); HostExec only when paired. Truncated output retry at lines 714 to 745. Doom loop tiers Nudge, StrongNudge, Escalate at lines 768 to 804. Concurrency safe tools start while the reply is still streaming.

Tool execution: the main loop uses Tools/LocalToolExecutor.cs (schema validation line 75, capability check line 194, permission check line 204, PreToolUse hook line 289, PostToolUse hook line 314). Tools/ToolDispatcher.cs is used only by PlaybookExecutor.

PermissionEngine (Permissions/PermissionEngine.cs): deny rules, stored grants, AutoAllow, Deny, allow rules, then prompt. Child engines for sub agents, playbook scopes.

Sessions: AcpSessionStore keeps a ConcurrentDictionary; files go to DataDirectory/sessions/<date>_<id>.jsonl plus checkpoints.json and index.json (Session/SessionManager.cs lines 14 to 152).

HookRunner (Hooks/HookRunner.cs): shell commands for PreToolUse, PostToolUse, SessionStart with "if" conditions. Exit code 2 blocks the call.

MemoryStore (Memory/MemoryStore.cs): DataDirectory/memory/*.md with YAML frontmatter, MEMORY.md is an auto index. The index goes into the system prompt (Utils/SystemPrompt.cs line 195).

System prompt: Base, or .openmono/system-prompt.md in the workspace, then DataDirectory/system-prompt.md (Core/Config/PromptOverrides.cs; SystemPrompt.cs line 182). OPENMONO.md project instructions are added (line 187), found by walking up, capped at 200 lines and 25 KB.

## 2. ACP API

Acp/AcpEndpoints.cs lines 22 to 34, HTTP plus SSE on port 7475:
GET /api/v1/discovery; GET and POST /api/v1/sessions (body {model?}); GET /sessions/{id}; GET /sessions/{id}/messages; POST /sessions/{id}/turn; POST /sessions/{id}/host {action on|off}; DELETE /sessions/{id}; GET /diffs, GET /diffs/{toolCallId}, POST /diffs/undo, POST /diffs/redo.

Turn body (lines 273 to 365) takes exactly one of: message, permission{id, decision allow|deny, scope once|session}, user_input{id, value}, playbookPermission, plan_decision, toggle_mode{id, decision}, mode plan|build, abort:true.

Pause types: Core/Acp/AcpServerSettings.cs line 52, enum PendingResponseKind { Permission, UserInput, ToggleMode, PlaybookApproval }. SSE pause events (Acp/AcpUserInteractionForwarder.cs): permission_request (id perm_), playbook_permission_request (pbk_), toggle_mode_request (mode_), user_input_request (ask_). A HOST_EXEC question adds kind=host_exec, command, as_root and so on (lines 144 to 160). Pauses time out after 10 minutes. Other events: text_delta, thinking_delta, tool_start, tool_end, plan_ready, compaction, checkpoint, usage, escalated, done, error (AcpTurnRunner.cs lines 716 to 855).

Many sessions per process: yes. Each AcpSession has its own TurnLock (AcpSession.cs line 36); a busy session returns 409 session_busy. New ACP sessions start in Plan mode.

What is shared by every session in one process: WorkingDirectory, DataDirectory (memory, sessions, prompt overrides), the single ILlmClient and its request gate, the ToolRegistry and AppConfig (ConversationLoopFactory.cs lines 56 to 71). session.Model is stored but ConversationLoop line 392 always sends _config.Llm.Model, so model is per process on main.

No auth on ACP. Kestrel binds localhost, or all interfaces inside Docker (Program.cs lines 283 to 284). Safety depends on publishing the port only to 127.0.0.1.

Config choice: settings.json in DataDirectory, then project .openmono/settings.json, then --config, then env, then model preset (Core/Config/ConfigLoader.cs). The AcpServer section is not merged from files, so ACP on/off and port come from flag or env (ACP_PORT, --acp-port).

## 3. agent-host (HostBridge)

HostDaemon.cs: HttpListener on 127.0.0.1 and the docker bridge gateway, never 0.0.0.0, daemon port 7476 (Session/HostGatewayBootstrap.cs line 12). Endpoints /ping, /status, /pair (pair_secret), /exec (token plus session id), /revoke, /verify, /keepalive, and /admin/invite (TTL 30 to 86400 s), /admin/requests, /admin/sessions with X-Pair-Secret. Idle token timeout 2 hours.
Pairing secret: ~/.openmono/host-bridge.json, mode 0600 (BridgeConfig.cs line 41). Containers pair by a TTL bound invite (OPENMONO_HOST_INVITE) or operator approval.
HostExec flow: model calls HostExec, which raises a user_input_request with a HOST_EXEC envelope; the ACP client answers; AcpTurnRunner lines 430 to 482 runs it via HostGateway. Daemon enforces deny list and sudo policy (Core/HostExec/SudoPolicy.cs); it does not re ask consent.
Audit: Core/HostExec/AuditLog.cs writes [host-exec] lines with redaction to ~/.openmono/logs/openmono-<date>.log on the host.
AgentContainer.cs: container name fixed as openmono-host-bridge (line 16), one container, mounts ~/.openmono. So it cannot spawn a roster as is.
Scheduler: none. No cron, routine or timer code beyond the session reaper and gateway heartbeat.
Protection folder: does not exist on main or on any upstream branch (checked with ls-tree on all 14 branches).

## 4. Remote inference and parallelism

Remote is supported today: set llm.endpoint and llm.api_key (docs/DEPLOYMENT-MODES.md Mode B1), or OPENMONO_ENDPOINT and OPENMONO_API_KEY. OpenAiCompatClient posts to {endpoint}/v1/chat/completions with Bearer key, 10 minute timeout, 3 retries (1, 4, 16 s).
Three limits stack:
1. Per agent process: a static SemaphoreSlim sized by Llm.MaxConcurrentRequests (default 2; OpenAiCompatClient.cs lines 17 to 19 and 47 to 65). LlmConfig.MergeFrom does not copy it, so settings.json cannot change it.
2. Inference server slots: INFERENCE_PARALLEL, default 1. On Strata each slot keeps its full context; 2 to 8 slots; each slot costs expert cache VRAM and per request speed (docs/STRATA.md lines 105 to 122). On llama.cpp, --parallel N splits context (docs/MODELS.md line 298).
3. Sub agents: Agents.MaxConcurrentAgents 2, MaxNestingDepth 3.
Flash-Next on Strata needs 24 GB+ NVIDIA and a 48 GB class box, about 80 to 160 tok/s single stream (docs/MODELS.md lines 32 and 168).

## 5. Tool call parsing and repair

Native tool_calls are accumulated by index (OpenAiCompatClient.cs lines 374 to 406). Bad JSON arguments are dropped (lines 195 to 205; ConversationLoop lines 626 to 632). Fallback: a Qwen XML regex for <function=name><parameter=x> (lines 27 to 32, applied at 230 to 250 and 361 to 364) when there are no native calls; ids call_xml_N, all values as strings. SchemaValidator then validates. No real JSON repair beyond that. llama-server runs with --jinja.

## 6. Seams that need no edits to existing files

Per bot env vars (OPENMONO_WORKSPACE, OPENMONO_DATA_DIR, ACP_PORT, OPENMONO_ENDPOINT, OPENMONO_MODEL_PRESET); .openmono/system-prompt.md and plan-prompt.md per workspace; OPENMONO.md; memory directory; hooks (PreToolUse as a policy or egress gate); MCP servers (handoff, shared memory, scheduler as tools); playbooks (Playbooks.Paths); permission rules in settings.json; the ACP HTTP API; the host daemon admin API. A roster service can live in a new top level folder and drive containers with docker compose run --name <bot> -p 127.0.0.1:<port>:7475 plus a per bot data dir.
Gotchas: compose container_name openmono-agent is fixed, so use run with --name or separate project names. Every container mounts the same ~/.openmono, so set OPENMONO_DATA_DIR per bot or bots share memory, sessions and prompts.

## 7. License, telemetry, branches

LICENSE: GNU AGPL v3. OpenMausBot is Apache 2.0 with a source available enterprise/ folder; Apache code can go into AGPL work with NOTICE kept, enterprise/ cannot be copied.
Telemetry: none in code. Outbound hosts are localhost, llama-server, caddy, host.docker.internal, api.openai.com, api.anthropic.com, html.duckduckgo.com, github.com. Relay URLs appear only in scripts and .env.example.
Upstream branches (last commit date): ctx-issue, git-auth-config, fix-img-paste (Nov 2); upgrade-cmd-imp (Oct 31); multi-model, main, two cursor/ doc branches, web-search-imp (Oct 29); Windows (Oct 9); feature/jev-cua (Sep 20); agent-prompts (Jul 30); agent-commands (Jul 29); fix-context-detection (Jul 13).
agent-mosaic and agent-host-imp do not exist upstream (git ls-remote). They may be in a fork or local only.
multi-model: 1 commit ahead, 4 behind, 36 files, +2437. Adds ~/.openmono/backends.json, BackendRegistry, LlmClientPool, RoutingLlmClient (per session model and backend), and ACP GET/POST/DELETE /api/v1/backends plus POST /sessions/{id}/backend. This fixes the per process model limit and fits a roster well.
feature/jev-cua: 9 commits ahead, 98 behind, 59 files, +4577. Adds Decisions (ChiefRouter routing to research, write, review workers; JobHandoff records queued at .openmono/decision-queue/<destination>/<uuid>.json), Decide* tools, DecisionGateTool, SpecialistTool, judge gates in playbooks. Uses the old Cli/Config layout, so it needs a rebase.
Windows: 13 ahead, 95 behind, +6942, all under windows/ (Supervisor, LAN inference with required API key, model downloader).
agent-prompts: queued prompt fix while a turn runs (4 ACP files). agent-commands: install script fixes.

## 8. Earlier questions: answered or still open

Windows bot (how agent and agent-host work, ACP endpoints, pause types, seams, add-on folder, protection folder): answered by sections 1 to 3 and 6. Protection folder does not exist upstream.
DeepSeek bot (box sizing, flags, tool calling under jinja, multi-model fit, agent-mosaic and agent-host-imp): flags, slot model, tool parsing and multi-model are answered. Still open: real sizing for N bots on Spencer's server, and those two branches.
SH offline-pi (proto, scheduler): not in this repo; OMA has no scheduler.
OMA Android (approval UI): the ACP pause events above are the contract; the app itself is not here.
Server Building Bot and Bonsai vs Flint: hardware tiers in docs/MODELS.md; Flint is not mentioned in the repo.

Still unknown: inference server address, GPU, RAM and current INFERENCE_PARALLEL; where agent-mosaic and agent-host-imp live; how reliable Flash-Next tool calls are under real load; whether ACP should get an auth token before exposing it beyond localhost; pair secret and API key rotation (a task for Spencer).

## Comparison note: OpenMausBot

TypeScript server with about 1370 files in server/, including bot-directory, bot-folder, peer-roster, handoff-ticks, memory-* and routine modules. It already models bots, routines and handoffs. Useful as a design reference for the roster layer; OMA would supply the sandboxed agent and host exec.
