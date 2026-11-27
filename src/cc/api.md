# Control Panel HTTP contract

Normative shapes for the OpenMonoAgent.ai Control Panel. The product reasons are in [control-panel-plan.md](control-panel-plan.md). How the server is built is in [PLAN.md](PLAN.md). Screen states that consume these routes are in [screens.md](screens.md).

JSON is snake_case, matching `JsonOptions.Default` and the existing ACP DTOs. Errors are:

```json
{ "error": "snake_case_code", "detail": "One sentence a person can read." }
```

`detail` is optional. Unknown fields on input are ignored. Missing required fields are `400` `{ "error": "invalid_body", "detail": "..." }`.

Unless a route says otherwise:

- Browser calls send `X-OpenMono-Request: 1` on `POST`, `PUT`, and `DELETE`.
- The panel SPA sends `X-OpenMono-Detach: 1` on turn posts.
- The VS Code extension sends neither header.

## 1. ACP routes that already exist

Implemented in `src/OpenMono.Cli/Acp/AcpEndpoints.cs` and `AcpTurnRunner.cs`. The panel uses them as they are, plus the two new routes in section 2.

### `GET /api/v1/discovery`

`200`

```json
{
  "version": "1.0.0",
  "agent_id": "agt_0123abcd",
  "host_workspace": "/home/me/proj",
  "container_workspace": "/home/me/proj",
  "status": "ready",
  "uptime_seconds": 12
}
```

`version` is hardcoded today. The panel task replaces it with the `VERSION` file plus the git revision. Do not add secrets to this object.

### `GET /api/v1/sessions`

`200`

```json
{
  "sessions": [
    {
      "session_id": "abc",
      "title": "first line of the chat",
      "started_at": "2026-11-27T00:00:00.0000000Z",
      "last_activity_at": "2026-11-27T00:10:00.0000000Z",
      "turn_count": 2,
      "model": "qwen",
      "message_count": 4,
      "summary": null
    }
  ]
}
```

### `POST /api/v1/sessions`

Body, optional:

```json
{ "model": "qwen" }
```

`200` `{ "session_id": "abc", "model": "qwen" }`. New sessions are in plan mode.

### `GET /api/v1/sessions/{id}`

`200`

```json
{
  "session_id": "abc",
  "model": "qwen",
  "started_at": "2026-11-27T00:00:00.0000000Z",
  "turn_count": 2,
  "plan_mode": true,
  "host_enabled": false,
  "thinking_level": "off",
  "thinking_enabled": false,
  "thinking_levels": ["off", "on"],
  "thinking_description": ""
}
```

`404` empty body when the id is unknown or fails the id check.

### `GET /api/v1/sessions/{id}/messages`

`200`

```json
{
  "messages": [
    {
      "role": "user",
      "content": "hello",
      "timestamp": "2026-11-27T00:00:00.0000000Z",
      "toolCalls": null,
      "thinking": null
    },
    {
      "role": "assistant",
      "content": "done",
      "timestamp": "2026-11-27T00:00:01.0000000Z",
      "thinking": "optional chain of thought",
      "toolCalls": [
        {
          "id": "call_1",
          "name": "FileRead",
          "summary": "{ \"path\": \"README.md\" }",
          "ok": true,
          "preview": "file text or null"
        }
      ]
    }
  ]
}
```

`toolCalls` is camelCase in this one DTO because `HistoryToolCallDto` sets `[JsonPropertyName("toolCalls")]` on the parent and camelCase names on the call. The panel client accepts that as-is. Do not rename it in the first slice. System and tool-role messages are omitted; tool results are folded into `toolCalls`.

### `DELETE /api/v1/sessions/{id}`

`204`. Cancels the turn if one is running. Unknown id is also `204`.

### `POST /api/v1/sessions/{id}/host`

```json
{ "action": "on" }
```

`action` is `on` or `off`. `200`

```json
{ "ok": true, "host_enabled": true, "run_as": "me", "sudo": "no" }
```

Pairing failure is still `200` with `{ "ok": false, "host_enabled": false, "error": "human sentence" }`. The MVP hides this route. The advanced switch in a later phase calls it. The wizard does not.

### Diffs

`GET /api/v1/sessions/{id}/diffs` → `200`

```json
{
  "diffs": [
    {
      "tool_call_id": "call_1",
      "file_path": "src/A.cs",
      "is_creation": false,
      "status": "applied",
      "timestamp": "2026-11-27T00:00:00.0000000Z"
    }
  ]
}
```

`GET /api/v1/sessions/{id}/diffs/{toolCallId}` → `200`

```json
{
  "tool_call_id": "call_1",
  "file_path": "src/A.cs",
  "is_creation": false,
  "status": "applied",
  "before": "old",
  "after": "new"
}
```

`POST /api/v1/sessions/{id}/diffs/undo` and `.../redo` → `200` `{ "ok": true, "file_path": "src/A.cs" }`. `ok: false` and an empty `file_path` when there is nothing to undo or redo.

### `POST /api/v1/sessions/{id}/turn`

`Content-Type: application/json`. Exactly one of these bodies.

| Body | Result |
|---|---|
| `{ "message": "text" }` | SSE stream. A leading `/` is a slash command. |
| `{ "abort": true }` | `204`. Does not take the turn lock. |
| `{ "permission": { "id": "perm_…", "decision": "allow", "scope": "once" } }` | SSE. `decision` other than `allow` is a deny. `scope` is `once` (default) or `session`. |
| `{ "playbookPermission": { "id": "pbk_…", "decision": "approve" } }` | SSE. Other decisions deny. |
| `{ "user_input": { "id": "ask_…", "value": "allow once" } }` | SSE. The field is `value`, not `answer`. Host consent values are the strings `HostConsent.Map` accepts: `allow once`, `allow all for session`, `allow 30m`, `allow today`, `deny`, `deny all`. |
| `{ "plan_decision": "auto" }` | SSE. `auto` implements and auto-approves writes. `gated` or `ask` implements and prompts per edit. Any other string keeps planning. |
| `{ "toggle_mode": { "id": "mode_…", "decision": "approve" } }` | SSE. |
| `{ "mode": "plan" }` | `200` `{ "mode": "plan" }` or `{ "mode": "build" }`. Not a stream. |

Busy session on a body that needs the lock: `409` `{ "error": "session_busy" }`. Unknown session: `404`. Bad JSON: `400` `{ "error": "invalid_json", "detail" }`. A body with none of the keys: `400` `{ "error": "invalid_body", "detail" }`.

SSE wire format from `SseWriter`, one event:

```
event: text_delta
data: {"content":"hi"}

```

Payloads the client must handle:

| event | data |
|---|---|
| `text_delta` | `{ "content": "..." }` |
| `thinking_delta` | `{ "content": "..." }` |
| `tool_start` | `{ "id", "name", "summary", "arguments" }` |
| `tool_status` | `{ "id", "status" }` |
| `tool_end` | `{ "id", "name", "ok", "duration_ms", "reason", "error_code" }` |
| `tool_result_preview` | `{ "id", "preview", "artifact_id" }` |
| `mode_changed` | `{ "mode": "plan" \| "build" }` |
| `plan_ready` | `{ "plan", "plan_path" }` |
| `permission_request` | `{ "id", "tool", "summary", "dangerous" }` |
| `playbook_permission_request` | `{ "id", "playbookName", "steps": [{ "id", "gate", "description" }], "tools": [{ "name", "isReadOnly", "dangerous" }], "requiresModeSwitch" }` |
| `toggle_mode_request` | `{ "id", "reason" }` |
| `user_input_request` | `{ "id", "question" }` or, for host exec, the same plus `kind: "host_exec"`, `command`, `cwd`, `as_root`, `timeout_ms`, `background` |
| `usage` | `{ "input_tokens", "output_tokens", "total_tokens", "context_tokens", "context_window", "gen_tps", "avg_tps" }` |
| `sub_agent_log` | `{ "line" }` |
| `compaction_started` | `{ "kind": "compaction", "status": "started", "title", "reason", "prompt_tokens" }` |
| `compacting` | `{}` |
| `compaction` | `{ "kind", "status": "done", "title", "reduction_pct", "messages_compressed", "duration_seconds", "checkpoint_index", "messages_before", "messages_after", "tokens_before", "tokens_after", "summary_text", "reason" }` |
| `checkpoint_started` | `{ "kind": "checkpoint", "status": "started", "title", "trigger", "prompt_tokens" }` |
| `checkpoint` | `{ "kind", "status": "done", "title", "trigger", "messages_compressed", "messages_kept", "duration_seconds", "checkpoint_index", "summary_text" }` |
| `thinking_changed` | `{ "kind": "thinking", "status": "changed", "level", "enabled", "levels", "description" }` |
| `output_truncated` | `{ "tool_name" }` |
| `escalated` | `{ "kind", "scope", "error_code" }` |
| `error` | `{ "message" }` |
| `done` | `{}` |

`playbookName`, `isReadOnly`, and `requiresModeSwitch` are camelCase because the anonymous objects in `AcpUserInteractionForwarder` use those identifiers and the SSE serializer does not apply the snake_case policy. The client accepts the names the server sends today.

With `X-OpenMono-Detach: 1`, each event also has an `id` line equal to the log seq:

```
id: 17
event: text_delta
data: {"content":"hi"}

```

## 2. New ACP routes

### `GET /api/v1/sessions/{id}/events?after={n}`

`Content-Type: text/event-stream`. Replays events with `seq > after`, then follows. `after` defaults to the `Last-Event-ID` request header, then to `0`. Does not take the turn lock and does not cancel the turn when the client leaves.

`409` `{ "error": "events_expired", "oldest_seq": 100 }` when `after` is older than the ring. `404` unknown session. `400` `{ "error": "invalid_after" }` when `after` is not an integer.

Comment heartbeats are lines `: ping` and are not events.

### `GET /api/v1/sessions/{id}/turn`

`200`

```json
{
  "busy": true,
  "last_seq": 42,
  "pending": [
    { "id": "perm_abc", "kind": "permission" }
  ]
}
```

`kind` is `permission`, `user_input`, `toggle_mode`, or `playbook_approval`, matching `PendingResponseKind`. `pending` is empty when nothing is waiting. `busy` is true while `TurnLock` is held.

## 3. Panel routes

Prefix `/api/v1/panel`. All of them require the loopback guard. They exist only when the process was started with `--panel`.

### `GET /api/v1/panel/bootstrap`

`200`

```json
{
  "role": "agent",
  "os": "windows",
  "arch": "x64",
  "version": "0.6.1",
  "revision": "e4b8eae",
  "port": 7475,
  "bind": "loopback",
  "docker": false,
  "inference_supported": false,
  "wizard_complete": false,
  "cli_present": false,
  "data_directory": "C:\\Users\\me\\.openmono"
}
```

`role` is `agent`, `inference`, `full`, or `unknown`. `os` is `windows`, `linux`, or `macos`. `inference_supported` is false on Windows. `cli_present` is true when the bash `openmono` was found. No keys.

### `GET /api/v1/panel/health`

`200`

```json
{
  "agent": { "state": "up", "accepting": true },
  "model": {
    "state": "up",
    "endpoint": "http://127.0.0.1:7474",
    "model_id": "qwen",
    "latency_ms": 40
  },
  "inference_process": { "state": "absent", "backend": null },
  "tunnel": { "state": "absent", "public_endpoint": null },
  "gpu": null,
  "settings_drift": false,
  "host_bridge": { "state": "absent" }
}
```

`model.state` is `up`, `down`, `unconfigured`, `auth`, or `unreachable`. `inference_process.state` and `tunnel.state` and `host_bridge.state` are `up`, `down`, or `absent`. `inference_process.backend` is `llama`, `strata`, `metal`, or null. `gpu` is null or `{ "name", "vram_used_mib", "vram_total_mib", "util_pct" }`. `agent.accepting` is false after the UI's Stop until Start.

This route never includes a key. `endpoint` is the URL already stored in settings, which the operator typed.

### `GET /api/v1/panel/model`

`200`

```json
{
  "source": "local",
  "endpoint": "http://127.0.0.1:7474",
  "model": "",
  "api_key_set": true,
  "active_provider": null
}
```

`source` is `local`, `remote`, `openai`, `anthropic`, or `ollama`. `active_provider` is the providers map key when one has `active: true`, else null (the built-in local client).

### `PUT /api/v1/panel/model`

```json
{
  "source": "remote",
  "endpoint": "http://10.0.0.8:7474",
  "model": "qwen",
  "api_key": "write-only, omit to keep the stored key"
}
```

`endpoint` must be an absolute `http` or `https` URL. `400` `{ "error": "bad_endpoint" }` otherwise. `source: local` forces the endpoint to `http://127.0.0.1:{port}` from inference settings, default port 7474. `openai`, `anthropic`, and `ollama` set that provider's `active: true` and clear `active` on the others, which is what `ProviderRegistry.CreateClient` reads. The response is the GET shape. The key is not in it.

### `POST /api/v1/panel/model/test`

No body. Uses the stored settings. `200`

```json
{ "ok": true, "model_id": "qwen", "failure": null }
```

or

```json
{ "ok": false, "model_id": null, "failure": "auth" }
```

`failure` is `down`, `auth`, `unreachable`, or `bad_endpoint`. `auth` means HTTP 401. `unreachable` means the TCP connection failed. `down` means a connection that returned a non-success status other than 401. Timeout is 5 seconds and counts as `unreachable`.

### `GET /api/v1/panel/project` and `PUT /api/v1/panel/project`

`200` / PUT body:

```json
{
  "path": "/home/me/proj",
  "mode": "folder",
  "docker_available": false,
  "confirm_root": false
}
```

`mode` is `sandbox` or `folder`. PUT with `mode: sandbox` when `docker_available` is false is `409` `{ "error": "docker_required" }`. PUT whose path is the user profile or the filesystem root is `409` `{ "error": "broad_folder" }` unless `confirm_root` is true. The path must exist and be a directory. `400` `{ "error": "not_a_folder" }` otherwise. Relative paths are `400` `{ "error": "path_not_absolute" }`.

### `POST /api/v1/panel/agent/start` and `POST /api/v1/panel/agent/stop`

Empty body. `200` `{ "accepting": true }` or `{ "accepting": false }`. Start is rejected with `409` `{ "error": "wizard_incomplete" }` until the wizard flag is set. Stop cancels the active turn via `CancelTurn` and rejects new messages with `409` `{ "error": "agent_stopped" }` on `POST .../turn` until start. Stop does not exit the process.

### `POST /api/v1/panel/jobs`

```json
{ "command": "start-inference", "confirm_token": null }
```

`202`

```json
{ "job_id": "job_0123abcd", "command": "start-inference", "state": "running" }
```

`409` `{ "error": "job_busy", "job_id": "job_existing" }` when a job is running.

`409` `{ "error": "needs_confirm", "confirm_token": "hex", "expires_at": "2026-11-27T00:02:00.0000000Z" }` for confirm commands. The token is not a bearer token and does not authorize any other route.

`409` `{ "error": "session_busy" }` for `upgrade-apply` while a turn is busy.

`400` `{ "error": "unknown_command" }`.

`501` `{ "error": "not_on_windows", "detail": "The model runs on another computer." }`.

`424` `{ "error": "cli_missing", "detail": "OpenMono is not installed on this computer." }`.

Allowed `command` values are the table in [PLAN.md](PLAN.md) section 4.

### `GET /api/v1/panel/jobs/{id}`

`200`

```json
{
  "job_id": "job_0123abcd",
  "command": "start-inference",
  "state": "succeeded",
  "exit_code": 0,
  "started_at": "2026-11-27T00:00:00.0000000Z",
  "finished_at": null
}
```

`state` is `running`, `succeeded`, or `failed`. `404` unknown id.

### `GET /api/v1/panel/jobs/{id}/logs?after={n}`

SSE.

```
id: 3
event: log
data: {"seq":3,"stream":"stdout","line":"Downloading model"}

```

`stream` is `stdout` or `stderr`. When the job reaches a terminal state, one last event:

```
event: job
data: {"state":"succeeded","exit_code":0}
```

Then the stream ends. `after` and `Last-Event-ID` work like the turn event stream.

### `GET /api/v1/panel/logs?source={name}&after={n}`

`source` is `panel`, `agent`, `inference`, or `tunnel`. `400` `{ "error": "bad_source" }` otherwise. Same SSE `log` events as jobs. `inference` and `tunnel` on Windows are `501` `not_on_windows`. Lines that look like secrets are redacted to `[redacted]` before they are stored.

### Phase 2 routes

Specified so names stay stable. They return `404` `{ "error": "not_ready" }` until that phase.

`POST /api/v1/panel/pairing/codes` → `201`

```json
{
  "code": "AB3K9Q2M",
  "expires_at": "2026-11-27T00:10:00.0000000Z",
  "endpoint": "http://192.168.1.20:7474",
  "panel_url": "http://192.168.1.20:7475"
}
```

`GET /api/v1/panel/pairing/codes/{code}` → `200` `{ "endpoint", "api_key" }` once, then `404`. `429` `{ "error": "rate_limited" }` after 5 failures from one IP in a minute.

`POST /api/v1/panel/connection`

```json
{ "mode": "pair", "host_panel": "http://192.168.1.20:7475", "code": "AB3K9Q2M" }
```

`mode` is `local`, `pair`, or `direct`. `direct` also sends `endpoint` and `api_key`. Response is the `GET /model` shape. `host_panel` must be `http` or `https` with an IP or host and a port. `400` `{ "error": "bad_host_panel" }` for any other URL, including `file:` and link-local metadata hosts the implementer denylists (`169.254.169.254`).

`POST /api/v1/panel/connection-card/reveal` → `200` `{ "endpoint", "api_key", "revealed": true }` for a loopback caller, once. Later calls are `409` `{ "error": "already_revealed" }`.

`GET /api/v1/panel/playbooks` → `200`

```json
{
  "playbooks": [
    { "name": "commit", "version": "1.0.0", "description": "...", "scope": "global", "user_invocable": true }
  ]
}
```

Running one is `POST .../turn` with `{ "message": "/playbook commit" }`, not a new executor.

`GET /api/v1/panel/files?path=` lists one directory inside the project. `path` is relative. `200`

```json
{ "path": "src", "entries": [ { "name": "A.cs", "kind": "file", "bytes": 10 } ] }
```

`GET /api/v1/panel/files/content?path=` returns `{ "path", "text", "truncated" }` for files up to 200 KB of text. Binary files are `{ "text": null, "binary": true }`. Paths that `PathGuard` rejects are `403` `{ "error": "outside_workspace" }`.

`GET /api/v1/panel/permissions` and `PUT /api/v1/panel/permissions` read and write the `permissions.tools` object already documented in `docs/CONFIG.md`. PUT replaces that object. It does not merge. The response is the stored object.

`POST /api/v1/panel/token/rotate` is loopback-only. `200` `{ "token": "shown once" }`. LAN mode stores the hash. The first slice does not enable LAN listen, so this route can exist and return `409` `{ "error": "lan_not_enabled" }` until phase 2.

## 4. Static UI

`GET /` and extensionless paths serve `wwwroot/index.html` when `--panel` is set. `GET /assets/*` serves files. A missing asset is `404`, not the HTML fallback.

`/api/` is never rewritten to `index.html`.
