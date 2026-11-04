# Phase 0 verification record

Builder: oma-bot executor, Nov 3, 2026 (America/Phoenix). Phase 0 of the plan (section 8) is mostly waiting on Spencer and on hardware. This file records what could be checked by reading OMA main (e4b8eae) on the build box, and what stays open because it needs the real inference box, the relay or Docker.

Status words: **Done** means checked here from source. **Open** means it needs Spencer, a code bot or real hardware.

| # | Task (plan section 8, phase 0) | Status | Result |
|---|---|---|---|
| 1 | Spencer answers 13.1 decisions | Partly done | D1, D2, D3, D5, D12 to D17 answered. D4, D6 to D11, D18 to D20 got a builder choice, recorded in decisions.md as "chosen by builder, needs Spencer review" |
| 2 | Relay 13.2 questions to code bots | Open | The builder may not message other bots. Questions stay in plan section 13.2 for Spencer |
| 3 | Multi-model branch ACP changes | Done (read only) | origin/multi-model adds `GET /api/v1/backends`, `POST /api/v1/backends`, `DELETE /api/v1/backends/{name}` and `POST /api/v1/sessions/{id}/backend`. The roster ACP client does not need them; the session create body carries `model` so the roster is ready when D4 lands |
| 4 | feature/jev-cua Decisions code | Open | Ideas only, not needed for v1 |
| 5 | Skip agent-mosaic and agent-host-imp | Done | Skipped (D2) |
| 6 | Hand-launch one agent container with own name, data dir, loopback port | Open (needs Docker) | Recipe written in bot/scripts/phase0-hand-launch.sh. Docker is not available on the build box |
| 7 | Config keys settable from files | Done | AppConfig.cs uses snake_case JSON (JsonOptions.cs). `agents.max_concurrent_agents`, `agents.max_nesting_depth`, `hooks.pre_tool_use`, `hooks.post_tool_use`, `hooks.session_start`, `mcp_servers.<name>.command/args/env/enabled`, `permissions.tools.<Tool>.allow/deny/ask`, `web.search` (string or bool) and `llm.endpoint/model/context_size` are all plain properties, so they load from settings.json |
| 8 | How hook templates quote `{{tool_input}}` (T13) | Done | HookRunner.cs replaces `{{key}}` with the raw value and runs `/bin/bash -c <command>`. There is no quoting. So the roster never puts `{{tool_input}}` or `{{tool_output}}` in a hook command. Blocking by input uses OMA's own `if.input_contains` condition with a fixed command, and only `'{{tool_name}}'` is passed, checked by the hook against a strict name pattern. Exit code 2 blocks the tool |
| 9 | Relay verification V1 to V5 | Open | Needs the Server Building Bot and the real relay. See below |
| 10 | Measure slots (task C1) | Open | Needs the real inference box. No slot count is written into any config. The gateway reads `concurrency.serving` or `/slots`, or uses a measured override recorded by `oma-bot gateway bench` |

## Relay tasks

| Task | Status | Note |
|---|---|---|
| V1 Which FRPS the main box uses, and its port | Open | Needs Server Building Bot |
| V2 How Spencer's own FRPS is installed | Open | Needs Server Building Bot |
| V3 Second TCP port through FRPC | Open | Needs a scratch setup and Spencer's go-ahead |
| V4 Plain HTTP on the gateway to relay leg | Builder choice | D20 builder choice: the gateway refuses a plain `http://` upstream that is not loopback unless roster.json sets `gateway.allow_plain_http_upstream: true`. Doctor reports that flag as a warning every run |
| V5 Relay limits for about 20 nodes | Open | Needs relay owner |

## ACP facts used by the roster (read from OMA source)

- Routes (AcpEndpoints.cs): `GET /api/v1/discovery`, `GET|POST /api/v1/sessions`, `GET|DELETE /api/v1/sessions/{id}`, `GET /api/v1/sessions/{id}/messages`, `POST /api/v1/sessions/{id}/turn` (SSE), `POST /api/v1/sessions/{id}/host`, diff routes.
- Turn body: exactly one of `message`, `permission {id, decision allow|deny, scope once|session}`, `user_input {id, value}`, `playbookPermission`, `plan_decision`, `toggle_mode {id, decision}`, `mode`, or `abort: true` (204). Busy session: 409 `{"error":"session_busy"}`.
- SSE frames: `event: NAME\ndata: JSON\n\n` (SseWriter.cs).
- Pause events (AcpUserInteractionForwarder.cs): `permission_request {id perm_*, tool, summary, dangerous}`, `playbook_permission_request {id pbk_*, playbookName, steps, tools, requiresModeSwitch}`, `toggle_mode_request {id mode_*, reason}`, `user_input_request {id ask_*, question}` and the host_exec form with `kind: "host_exec", command, cwd, as_root, timeout_ms, background`.
- Compose shadowing (docker/docker-compose.yml, service `agent`): `/dev/null` over `/home/agent/.openmono/host-bridge.json`, tmpfs on `/home/agent/.openmono/host-bridge/logs` and `/home/agent/.openmono/logs`, no docker.sock by default. The roster drift test (INT-08) reads this file and checks the rendered spec keeps every rule.
- Resume rules (AcpTurnRunner.cs): a host_exec pause is answered with `user_input {id, value}` where value is `HOST_EXEC_APPROVE` or `HOST_EXEC_DENY`; any other value denies. `toggle_mode` decisions are `approve` or `deny`. Answering a pause id that is unknown or already resolved returns 400 `{"error":"resume_error"}`. A pause ends the SSE stream without a `done` event, so the roster treats "stream ended after a pause event" as paused and "stream ended with neither" as a cut stream.
- Session digests (`GET /api/v1/sessions/{id}`) carry `turn_count` and `message_count`; usage frames carry `input_tokens`, `output_tokens` and `total_tokens`.
- Tested by INT-01 against a fake ACP server that follows these shapes (bot/src/Omabot.Tests/Support/FakeAcpServer.cs). INT-02 against the real agent image needs Docker and is skipped on the build box.
