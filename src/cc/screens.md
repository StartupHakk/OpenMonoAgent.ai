# Control Panel screens

States for the MVP UI. Routes that feed them are in [api.md](api.md). The shell is one page with a health strip that is always visible after the wizard, and a main region that swaps screens. Chat is not available until the wizard sets `wizard_complete`.

Copy is short and literal. Buttons are verbs. Errors show `detail` from the API when it is present, otherwise one sentence from the table below.

## Shell

Visible on every screen after the process is up.

| State | What the person sees |
|---|---|
| `loading` | A blank main region and the word "Connecting". Bootstrap has not returned. |
| `down` | "The panel is not answering." Retry calls `GET /api/v1/panel/bootstrap` again. |
| `ready` | Health strip plus the current screen. |

The health strip is five dots, each `up` (green), `down` (red), `absent` (grey), or `unknown` (grey, while the health request is in flight).

| Dot | Source field | Click |
|---|---|---|
| Agent | `agent.state` | Opens logs with `source=agent` |
| Model | `model.state` | Opens the connect screen |
| Model process | `inference_process.state` | Opens the agent-host card. Hidden when `inference_supported` is false |
| Tunnel | `tunnel.state` | Hidden in the MVP. Phase 2 opens the relay card |
| This page | always `up` when bootstrap succeeded | Opens logs with `source=panel` |

`model.state` of `auth` uses the red dot and the word "Key rejected". `unreachable` uses the red dot and the word "Can’t reach the model".

## Setup wizard

Shown when `wizard_complete` is false. Three steps. Back is always allowed. The checklist in the sidebar is the same three steps and is removed when the wizard finishes.

### Step 1 — This computer

| State | What the person sees |
|---|---|
| `choose` | Three choices when `inference_supported` and `os` is not `windows`: "It runs the model", "It runs the agent", "It does both". Windows shows only "It runs the agent" and the sentence "The model runs on another computer." |
| `saving` | The Next button is disabled. |
| `error` | PUT of the role failed. The choices stay. |

The choice writes `role` on bootstrap via the model/project flow's stored panel.json. It only decides which cards the page shows. The panel never installs anything.

### Step 2 — Connect the model

This is the connect screen, embedded in the wizard and available later from the health strip. See the next section. The wizard cannot advance while the last test is not `ok`.

### Step 3 — Project folder

See the project picker. Finish writes `wizard_complete: true` only after the project PUT returns `200` and the model test is `ok`. Finish then navigates to a new empty chat.

## Connect the model

| State | What the person sees |
|---|---|
| `loading` | Fields disabled until `GET /model` returns. |
| `edit` | A source picker: "This computer", "Another computer I own", "OpenAI", "Anthropic", "Ollama". Endpoint and model fields. Key field is empty even when `api_key_set` is true, with the hint "A key is saved" or "No key yet". |
| `testing` | Test button disabled. |
| `ok` | "The model answered." plus the `model_id`. |
| `auth` | "The key was rejected." The key field is focused. |
| `unreachable` | "This computer can’t reach that address." |
| `down` | "The address answered, but the model is not running." |
| `bad_endpoint` | "Enter an address that starts with http:// or https://." |
| `local_unsupported` | Source "This computer" on Windows: the choice is disabled and the sentence from step 1 is shown. |

Test calls `PUT /model` then `POST /model/test`. Leaving the key blank on PUT keeps the stored key.

Phase 2 adds a "Pair" state: two fields, panel URL and code, and the failure "That code is wrong or expired." The MVP uses "Another computer I own" as a typed endpoint and key only (`mode` is not sent; PUT `/model` with `source: remote` is the MVP path).

## Project picker

| State | What the person sees |
|---|---|
| `empty` | A path field. On Windows, Browse opens the tray folder dialog and fills the field. On Linux and macOS, Browse is hidden in the MVP and the person types a path. |
| `checking` | Save disabled. |
| `ready` | The path and a mode line. If `docker_available`, the mode is "Keep the agent in a box" (`sandbox`) by default, with "Work in the folder directly" as the other choice. If Docker is absent, the mode is fixed to `folder` and the line is "The agent can read and write this folder." |
| `broad_folder` | "That folder is your whole home or disk. Choose a project folder." A second button "Use it anyway" sends `confirm_root: true`. |
| `not_a_folder` | "That folder doesn’t exist." |
| `docker_required` | "Docker isn’t running, so the box option isn’t available." The mode flips back to `folder`. |

Save is `PUT /project`.

## Chat

One column of messages, a composer at the bottom, a diff drawer on the right when a file change exists. The route is `/chat/{session_id}`. No session id opens `POST /sessions` then redirects.

| State | What the person sees |
|---|---|
| `empty` | The composer and "Ask for something. The agent will look around before it changes files." Plan mode is on. |
| `streaming` | Assistant text growing from `text_delta`. Thinking is a collapsed row if any `thinking_delta` arrived. The Stop button is enabled. |
| `tool` | A card per `tool_start`: name, summary, a spinner. `tool_status` replaces the spinner text. `tool_end` sets ok or failed and shows `reason` when `ok` is false. `tool_result_preview` is an expander. `sub_agent_log` lines append inside the Agent card that is still spinning. |
| `waiting` | An approval card, below. The composer is disabled except Stop. |
| `compacting` | A single line "Compacting context…" from `compaction_started`, replaced by the `compaction` title when it finishes. Same for checkpoint. |
| `truncated` | A banner "The reply was cut off" from `output_truncated`. |
| `escalated` | A banner with `kind` and `error_code` and the sentence "The agent stopped and is waiting for you." The composer enables. |
| `error` | The `error` event `message`, in the transcript, not a modal. |
| `done` | Stop hides. Composer enables. `usage` updates the meter under the header: "12k / 168k" and the tokens-per-second figure when `gen_tps` is non-zero. |
| `reconnecting` | The stream dropped. The client opens `GET .../events` with the last id. The transcript stays. |
| `events_expired` | The client `GET`s messages, replaces the transcript, and follows events from `last_seq`. |
| `stopped` | `agent.accepting` is false. Composer disabled. Banner "The agent is stopped." Start in the strip's agent click target calls `POST /agent/start`. |
| `busy` | A send that got `409 session_busy` shows "The agent is still working." The client does not retry on its own in the MVP. |

Reload in `streaming` or `waiting` uses `events?after=` and must land back in the same state. That is the detach requirement.

Composer: text area, Send, and a paperclip for images when health later grows a vision flag. The MVP paperclip can be hidden. `@` inserts the typed path into the message; the server already resolves file mentions. The client does not read the file.

Plan / Build is a switch that `POST`s `{ "mode": "plan" }` or `{ "mode": "build" }` and also follows `mode_changed` from the server. The server wins if they disagree.

## Approvals

Rendered in the `waiting` state. One card at a time, matching the pause id.

### Tool

From `permission_request`.

| State | What the person sees |
|---|---|
| `ask` | Title is the tool name. Body is `summary`. Buttons: "Allow once", "Allow for this chat", "Deny". |
| `dangerous` | Same, with a red title, when `dangerous` is true. Same three buttons. |
| `sending` | Buttons disabled. |
| `failed` | "Couldn’t send that choice." Buttons return. |

Posts:

- Allow once → `{ "permission": { "id", "decision": "allow", "scope": "once" } }`
- Allow for this chat → `scope: "session"`
- Deny → `decision: "deny"`, `scope: "once"`

### Playbook

From `playbook_permission_request`. Lists `steps` and `tools`. Buttons: "Run it" (`decision: "approve"`) and "Don’t run it" (any other decision). `requiresModeSwitch` adds the sentence "This will switch to Build mode."

### Plan

From `plan_ready`. The plan text is the card body.

| Button | `plan_decision` |
|---|---|
| Do it | `auto` |
| Ask me before each edit | `gated` |
| Keep planning | `keep` |

### Question

From `user_input_request` without `kind`. The `question` text and a text field. Submit sends `{ "user_input": { "id", "value": "<text>" } }`.

### Host command

From `user_input_request` with `kind: "host_exec"`. Shows `command`, `cwd`, and "Runs as administrator" when `as_root` is true. Buttons and the `value` they send:

| Button | value |
|---|---|
| Allow once | `allow once` |
| Allow for this chat | `allow all for session` |
| Allow 30 minutes | `allow 30m` |
| Allow today | `allow today` |
| Deny | `deny` |
| Stop host mode | `deny all` |

The MVP does not show this card unless a turn emits it. The wizard does not turn host mode on.

### Mode switch

From `toggle_mode_request`. The `reason` and buttons "Switch" (`decision: "approve"`) and "Stay" (anything else) on `{ "toggle_mode": { "id", "decision" } }`.

Every approval card shows a countdown from 10 minutes, which is `PendingUserResponseTimeoutMinutes`. At zero the card says "The wait expired. Send a message to continue." The server has already failed the pause; the client does not invent a deny.

## Sessions

Route `/chats`.

| State | What the person sees |
|---|---|
| `loading` | "Loading chats." |
| `empty` | "No chats yet." and "New chat". |
| `list` | Each digest: title, relative time from `last_activity_at`, model. Click opens `/chat/{id}`. |
| `waiting` | Phase 2. A row whose `GET .../turn` has a non-empty `pending` shows "Waiting for you". The MVP list does not poll turn state. |
| `delete` | A row action. Confirm "Delete this chat?" then `DELETE`. Failure keeps the row and shows "Couldn’t delete that chat." |

New chat is `POST /sessions` then navigate.

## Logs

Route `/logs?source=`.

| State | What the person sees |
|---|---|
| `live` | Lines from the SSE log, newest at the bottom, follow on. |
| `paused` | Follow off. The person scrolled up. A "Jump to latest" button turns follow on. |
| `empty` | "No log lines yet." |
| `unavailable` | Windows `inference` or `tunnel`: "Logs for the model computer are on that computer." |
| `redacted` | No special UI. Lines already have `[redacted]` in them. |

Source picker: "This page", "Agent", and, when `inference_supported`, "Model".

## Agent-host card

The agent-host card (role `inference` or `full`, and `inference_supported`):

| State | What the person sees |
|---|---|
| `stopped` | "The model is stopped." Start. |
| `starting` | Job log. |
| `up` | "The model is running." plus `model_id` from health. Stop asks "Stop the model on this computer?" and then sends the confirm token. |
| `drift` | "Settings changed and are not applied yet." Start applies them. Shown when `settings_drift` is true. |
| `failed` | Last log lines and Retry. |

Start is `start-inference`. Stop is `stop-inference` with the confirm dance. The agent card on Windows does not show Start/Stop for a model. It shows Agent Start/Stop (`/agent/start` and `/agent/stop`).

Upgrade preview is a row on this card in the MVP: "Check for updates" runs `upgrade-check` and shows the job log. Apply is not on the MVP card.

## Settings

Route `/settings`.

| State | What the person sees |
|---|---|
| `view` | Port, bind ("This computer only"), version, revision, and a link "Source code" to the GitHub repository at that revision. |
| `lan_locked` | A switch "Allow other computers to open this page" that is disabled, with "Not available in this version." Phase 2 enables it and then demands a token and TLS. |
| `saving` | Disabled controls. |
| `error` | "Couldn’t save settings." |

The MVP settings screen is read-only except the checklist dismiss, which is `localStorage` only. Port changes are a later task because they require a restart of the listener.

Theme is a local toggle, light or dark, stored in `localStorage` under `openmono.panel.theme`. It is not a server setting.
