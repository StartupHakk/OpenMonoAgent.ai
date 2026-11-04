# Decisions

Spencer Thomason's decisions for the OMA bot roster. Dates are America/Phoenix. The plan (OMA-BOT-ROSTER-PLAN.md, section 13.1) lists the open items.

| # | Date | Decision |
|---|---|---|
| 1 | Nov 3, 2026 | OMA main is the only base. The Windows branch is ditched as a base. All roster work lives in the top-level bot/ folder. |
| 2 | Nov 3, 2026 | The oma-bot branch is rebuilt from main with only plan docs in bot/. Rewriting its history and force pushing it is approved. No other branch is touched. |
| 3 | Nov 3, 2026 | head-honcho is the lead bot and supervises all other bots. |
| 4 | Nov 3, 2026 | A separate, dedicated, read-only health bot checks the roster service, the gateway, inference reachability through the relay, each container, disk and queues. It reports to head-honcho and Spencer and cannot approve anything. The health check command keeps the name `oma-bot doctor`. |
| 5 | Nov 3, 2026 | The roster can be hosted as a web instance in both modes, single-owner and multi-user, from one code base, and it must be very secure. |
| 6 | Nov 3, 2026 | Licensing: everything is AGPL-3.0. Other repos were only read for ideas and nothing was copied. Revisit if code is ever copied. |
| 7 | Nov 3, 2026 | Hardware: the main project baseline is an RTX 3090 (24 GB) with 64 GB RAM running Qwen3.8-Flash-Next on Strata. Anything smaller is not enough for the main project. Fleet nodes may be smaller machines running weaker models. Slot counts are measured, not assumed. |
| 8 | Nov 3, 2026 | No bot ever approves its own permission requests or its own jobs. Approvals go to a human or to a separate approver bot that is not the requester and not a bot the requester controls. |
| 9 | Nov 3, 2026 | Secrets are stored locally on the box with restricted permissions, isolated per bot, redacted in memory and logs, and never sent to cloud inference. No rotation work is planned. |
| 10 | Nov 3, 2026 | All bot containers run on one host by default. Cross-host or fleet setup needs explicit human direction and is never decided by the roster or a bot. Fleet mode (about 20 OMA servers) is optional. |
| 11 | Nov 3, 2026 | The inference box is often not the bot host. Containers reach it through the gateway, and the gateway reaches it through the OMA relay (FRPS and FRPC). Local inference is never assumed. |
| 12 | Nov 3, 2026 | Tailscale is never used. The FRPS and FRPC relay is used for the main box and for fleet connections. |
| 13 | Nov 3, 2026 | agent-mosaic and agent-host-imp live on Joshua's branch and are out of scope for now. Revisit only if something genuinely useful turns up. |

## Builder choices (chosen by builder, needs Spencer review)

Spencer told the builder on Nov 3, 2026 to run all phases. Where the plan left a decision open, the builder took the safest option below and kept going. Every row here is "chosen by builder, needs Spencer review". None of them is final until Spencer confirms or changes it.

| # | Date | Plan item | Builder choice (chosen by builder, needs Spencer review) |
|---|---|---|---|
| B1 | Nov 3, 2026 | D20, relay leg over plain HTTP (V4, T29) | The gateway refuses a plain `http://` upstream that is not loopback unless roster.json sets `gateway.allow_plain_http_upstream: true`. Doctor prints a warning on every run while the flag is on. Preferred fix once V4 is answered: an `https://` relay endpoint or an FRP `stcp` visitor on the roster host so the leg is inside the TLS tunnel |
| B2 | Nov 3, 2026 | D6, roster home and workspace roots | `~/.omabot` and `/srv/work`, as suggested in the plan |
| B3 | Nov 3, 2026 | D9, error notices | Notices go to the `notices` table, shown by `oma-bot notices` and the web view. An optional local command (`notify.command` in roster.json) is off by default |
| B4 | Nov 3, 2026 | D10, HostExec | No bot gets `host_exec` in the default team file. The invite code path exists and is used only for bots that set `host_exec: true` |
| B5 | Nov 3, 2026 | D11, desktops | Desktop code and compose files are built, but `desktop: off` is the default and nothing starts a desktop unless the team file asks |
| B6 | Nov 3, 2026 | D18, web view technology | Plain HTML and ES modules served by the roster, no framework and no build step, strict Content Security Policy with no inline script |
| B7 | Nov 3, 2026 | D19, approver bots | `approvals.approver_bots` is empty. Spencer approves everything. When a row is added later, the broker still refuses the requester, its ancestors, descendants, managed and created bots |
| B8 | Nov 3, 2026 | D4, multi-model branch | Not adopted. The ACP client sends `model` on session create and ignores the backends routes until the branch reaches main |
| B9 | Nov 3, 2026 | Plan 6.4 folder list | Shared logic lives in an extra library, bot/src/Omabot.Core, so the CLI, supervisor, egress gate, MCP shim and node agent share one tested code base. bot/src/Omabot stays the service and CLI. The MCP shim project lives in bot/mcp-shim as the plan lists |
| B10 | Nov 3, 2026 | T13 hook quoting (phase 0 task 8) | OMA substitutes hook templates without quoting, so roster hooks never use `{{tool_input}}` or `{{tool_output}}`. Input checks use OMA's `if.input_contains` with fixed commands; only `'{{tool_name}}'` is passed and the script checks it against a strict pattern |
| B11 | Nov 3, 2026 | Phase 3 memory load path | The memory block is rendered into OPENMONO.md before each turn, and recall notes are placed in the message by the roster. The MCP `memory_read_topic` tool is the fallback. session_start is not used (L7) |
| B12 | Nov 3, 2026 | Plan 6.7 token passing | The Docker Engine API has no env file option, so the per-bot gateway token is passed in the container env. Residual exposure: visible to `docker inspect` for users in the docker group (root equivalent anyway). It is a per-bot gateway token, never the real inference key, and it is revoked by `oma-bot secrets rotate-bot BOT` |
| B13 | Nov 3, 2026 | D8, tool call target | 95 percent valid call rate on the task set, as a placeholder |
| B14 | Nov 3, 2026 | Fleet guard | Fleet commands refuse unless roster.json has `placement.mode: fleet` and `fleet.enabled_by` names the human who turned it on. Fleet commands exist only in the CLI, never as bot tools |
| B15 | Nov 3, 2026 | Web multi-user | `oma-bot web enable --mode web-multi` refuses unless the release gate passed and a signed off record exists (`~/.omabot/web/release-signoff.json`, written by `oma-bot web signoff` with Spencer's name). Outside review stays a manual step |
| B16 | Nov 3, 2026 | Web login methods | v1 web login is password plus TOTP. Passkeys are deferred; the users table has a column ready for them |
| B17 | Nov 3, 2026 | Cron engine | The Cronos package (MIT, a dependency, not copied code) handles cron with zones, `L`, `W` and `#`, and DST rules |
| B18 | Nov 3, 2026 | Streaming with tool repair | When a request offers tools, the gateway buffers the upstream stream, repairs the tool calls, then streams the repaired reply to the bot. Requests without tools stream straight through |
| B19 | Nov 3, 2026 | Plan 7.6 crash backoff ("after 4 failures in 30 minutes, disable" next to the four delays) | Each of the four delays (5 s, 30 s, 2 min, 10 min) is used once; the fifth crash inside 30 minutes disables the bot and writes one error notice. Read literally, disabling on the fourth crash would make the 10 minute step unreachable |
| B20 | Nov 3, 2026 | Pre tool hook without a tool list | The pre tool hook fails closed: if the bot's allowed-tools.txt cannot be read, every tool is blocked |
| B21 | Nov 3, 2026 | Plan 7.9 slots before measurement; gateway priority without a known turn | Chosen by builder, needs Spencer review. When no measured number exists and the server does not report its slots, the gateway runs one lane (the inference server default) and raises a notice until `oma-bot gateway bench --save` is run. When the gateway has no turn context for a bot, the request is treated as `assignment` (`gateway.default_priority`). Bots cannot set their own priority; the CLI, scheduler and handoff runtime record it in roster.db |
| B22 | Nov 3, 2026 | Labels of the implicit main backend | Chosen by builder, needs Spencer review. When only `gateway.upstream` is set, the gateway builds a backend named `main` with `gateway.upstream_labels`, default `[class=main-3090]`, so the default team's `flash-next` model routes to the main box through the relay |
| B23 | Nov 3, 2026 | "Background dropped when the queue is longer than S" | Chosen by builder, needs Spencer review. A background request is dropped (HTTP 429 `queue_full`) when adding it would make the waiting queue longer than S. A background request that finds a free lane and an empty queue runs at once |
| B24 | Nov 3, 2026 | Usage metering on streamed answers | Chosen by builder, needs Spencer review. The gateway does not add `stream_options` to bot requests (that would change the chunks OMA receives). It reads the server's usage block when present, and otherwise counts streamed content chunks and marks the row as estimated |
| B25 | Nov 3, 2026 | Which memory entries count as health and safety facts | Chosen by builder, needs Spencer review. An entry whose text starts with `Safety:` or `Health:` or carries `[safety]` or `[health]` never moves automatically (archive, tidy, organize) |
| B26 | Nov 3, 2026 | Per-turn shim token delivery | Chosen by builder, needs Spencer review. Each bot has its own shim socket, mounted only into its container, so the socket names the bot. The shim claims a token on that socket; the roster mints one only while the bot has a turn the roster started, binds it to that turn and drops it when the turn ends or a new one starts. Tokens live only in memory. A process inside the same container can also claim one, which grants nothing beyond that bot's own tools |
| B27 | Nov 3, 2026 | When organize runs | Chosen by builder, needs Spencer review. Organize (a model call) runs after capture and nightly only when MEMORY.md is past half of its line or byte budget, to save inference |
| B28 | Nov 3, 2026 | Instruction history undo | Chosen by builder, needs Spencer review. Memory files are journaled with undo now. Instructions and personality come from the team file, whose history is the audit log plus `oma-bot export`; undo for instructions arrives with the web view editor in phase 9 |
| B29 | Nov 3, 2026 | Direct writes to memory files | Chosen by builder, needs Spencer review. Bots are denied direct writes to memory files by permission rules (phase 1). As a backstop the roster rescans memory files after each turn and every service tick (the post tool hook leaves a flag), rewrites any secret through the redactor and journals it as `redacted after direct write`, without keeping the before text |
| B30 | Nov 3, 2026 | Roster memory and OMA native memory | Chosen by builder, needs Spencer review. Both stay on; the bot's instructions point it at the roster memory tools. Whether two stores confuse the model is a live measurement (needs the main box), so the note for Spencer is deferred to that measurement |
