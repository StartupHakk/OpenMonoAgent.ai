# Control Panel implementation plan

Build-ready detail for the product described in [control-panel-plan.md](control-panel-plan.md). This file is the order of work. The wire contract is [api.md](api.md). Screen states are [screens.md](screens.md). No panel code ships with these documents.

Reviewed against `main` `e4b8eae` and the ACP server in `src/OpenMono.Cli/Acp/`. Sizes are engineering size, not a calendar: **S** is one type and its tests, **M** crosses the CLI and the new panel project or changes turn lifetime, **L** is a new process model and is not in the first slice.

## 1. First slice

A person can open a browser on the machine that runs the panel, finish a short wizard, point the agent at a model, pick one project folder, and chat. The same page can start and stop the model where the `openmono` CLI exists, and can show health, logs, and the install log. Windows runs the agent side only. The VS Code extension, `scripts/acp-smoke.sh`, and `OpenMono.HostBridge` keep working against the same port without sending any new header.

Out of the first slice, on purpose: pairing codes, the relay card, upgrade Apply, the file tree, the playbook picker, a task queue, schedules, and more than one project. Those are later tasks in section 9. The HTTP shapes for them are still specified in [api.md](api.md) so the first routes do not have to be renamed.

## 2. Folder and project layout

```
src/cc/
  README.md
  control-panel-plan.md
  PLAN.md
  api.md
  screens.md
  OpenMono.Panel/                  # added in the first code PR, not before
    OpenMono.Panel.csproj          # net10.0 class library, registered in OpenMono.sln
    PanelServices.cs               # jobs, health probes, settings writes
    PanelCommandRunner.cs          # allowlisted openmono wrapper
    PanelEndpointMapper.cs         # /api/v1/panel/* that does not need ACP types
    PairingStore.cs                # phase 2
    wwwroot/                       # vite build output, copied here, served by Kestrel
  client/                          # Vite + React + TypeScript, build-time only
    index.html
    src/
      main.tsx
      api/                         # the only module that calls fetch
      screens/
      components/
```

Rules:

- Do not add `OpenMono.Panel.csproj` until the same change adds it to `OpenMono.sln`. A project file that is not in the solution is still a stray build input if someone runs `dotnet build src/cc/OpenMono.Panel`. The docs-only tree has no `.csproj`, no `package.json`, and no `global.json` of its own.
- `Directory.Build.props` applies to every project under the repo. The panel project inherits `net10.0`, nullable, and warnings-as-errors. It does not get its own copy of those properties.
- Dependency direction is one way. `OpenMono.Panel` references `OpenMono.Core` only. `OpenMono.Cli` references `OpenMono.Panel`. The panel library must not reference the CLI, or the projects cycle. Types that already live on the session (`AcpSession`, `AcpSessionStore`, `AcpTurnRunner`) stay in `src/OpenMono.Cli/Acp/`. The CLI maps the chat routes and calls the panel library for jobs, health, and settings.
- The SPA calls the server only from `client/src/api`. Components receive data as props. That is the Agent Canvas boundary worth keeping, without importing that repository.
- Node is a build machine dependency. The published agent does not ship `node` or `npm`. CI of the panel, when the client exists, runs `npm ci` and `vite build` in a job that is separate from `dotnet test`, then copies `dist/` to `wwwroot` before `dotnet publish`.

`OpenMono.Panel.csproj` contents when it is added:

- `TargetFramework` inherited.
- `ProjectReference` to `src/OpenMono.Core/OpenMono.Core.csproj`.
- No `FrameworkReference` to ASP.NET unless a type in this library must mention `WebApplication`. Prefer keeping `Map` methods that take `WebApplication` in the CLI, and keep this library free of Kestrel types. `PanelServices` is plain C#: start a process, write JSON, probe a URL.
- `wwwroot/**` as `Content` with `CopyToOutputDirectory` PreserveNewest, and as an embedded fallback so a single-file publish still has the UI. The first code PR can use copy-to-output only. Embedded resources are a packaging task, not a blocker for the wizard.

CLI files the first code PR touches:

| File | Change |
|---|---|
| `src/OpenMono.Cli/Program.cs` | Parse `--panel`. It implies `--acp-only` and turns on static files and `/api/v1/panel`. |
| `src/OpenMono.Cli/Acp/AcpServer.cs` | Call the loopback guard, then static files, then existing ACP routes, then panel routes, then the SPA fallback. |
| `src/OpenMono.Cli/Acp/AcpEndpoints.cs` | Detach opt-in on `POST .../turn`. Add `GET .../events` and `GET .../turn`. |
| `src/OpenMono.Cli/Acp/AcpSession.cs` | Own the turn `CancellationTokenSource` and the event log. Disconnect removes a subscriber. It does not cancel when the client opted in. |
| `src/OpenMono.Cli/Acp/TurnEventLog.cs` | New. Monotonic `seq`, ring of events, subscriber list. |
| `src/OpenMono.Cli/Acp/LoopbackGuardMiddleware.cs` | New. Host, Origin, and the custom header. See section 7. |
| `src/OpenMono.Cli/Acp/SseWriter.cs` | When the turn is detached, write `id: {seq}` before `event:`. Unchanged otherwise. |
| `openmono` | New `panel` subcommand. |
| `OpenMono.sln` | Project entry for `OpenMono.Panel` in the same commit as the csproj. |

## 3. Agent changes

### 3.1 Turns outlive the HTTP connection

Today `PostTurn` in `AcpEndpoints.cs` creates a `CancellationTokenSource` linked to `HttpContext.RequestAborted`, registers it with `AcpSession.RegisterTurn`, and writes SSE on that response. `AcpSession` documents that an SSE disconnect cancels the turn. A browser refresh would kill the agent. The extension, the smoke test, and the host bridge rely on that cancel, so the new behavior is opt-in.

Header: `X-OpenMono-Detach: 1` on `POST /api/v1/sessions/{id}/turn`.

| Header | Token source | On disconnect |
|---|---|---|
| Absent | Linked to `RequestAborted`, as today | `CancelTurn()` |
| `1` | A session-owned `CancellationTokenSource`, not linked to the request | Remove this response from the subscriber list. The turn keeps running. |

`CancelTurn` still runs for `{"abort":true}`, `DELETE /sessions/{id}`, and process shutdown. Those three stay the only ways a detached turn stops.

The turn lock does not change. A second `POST` with `message` while the lock is held still returns `409` and `{ "error": "session_busy" }`. Resume bodies (`permission`, `user_input`, `playbookPermission`, `plan_decision`, `toggle_mode`) are how a paused turn continues. They take the lock after the pause has released it, which is the current `PostTurn` order: abort does not take the lock; a new message does.

Implementation sketch inside `PostTurn`, after the body is parsed and the lock is acquired:

1. `var detach = HeaderIsOne(ctx, "X-OpenMono-Detach");`
2. `var turnCts = detach ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);`
3. `session.RegisterTurn(turnCts);`
4. Build the sink as `new FanoutSink(session.Events, responseWriter)`.
5. In `finally`, if the request abort fired and `detach` is false, behavior is unchanged because the token is already cancelled. If `detach` is true, `finally` still saves the session and releases the lock when the turn method returns. It must not call `CancelTurn` because the request ended.

`FanoutSink` implements `IAcpEventSink`. Each method appends one record to `TurnEventLog` and then writes SSE to every current subscriber. The original response is subscriber 0. `GET .../events` adds another.

`TurnEventLog` shape:

```
record TurnEvent(long Seq, string Name, string DataJson, DateTime Utc);
```

- `Seq` starts at 1 for each session and never resets while the process is up.
- Cap the ring at 2,000 events. If a subscriber asks for `after` below the oldest retained seq, the response is `409` with `{ "error": "events_expired", "oldest_seq": N }`. The client then reloads `GET .../messages` and resumes from `last_seq`.
- The log is in memory. The JSONL transcript remains the durable history. Do not write a second event database.
- `done` and `error` are normal events. They do not clear the log.

`GET /api/v1/sessions/{id}/events?after={n}`:

- Does not take `TurnLock`.
- Replays `seq > after`, then blocks for new records until the client disconnects.
- Writes `id:`, `event:`, `data:` so `EventSource` can send `Last-Event-ID` on reconnect. Honor `Last-Event-ID` when `after` is absent.
- Every 15 seconds with no event, write a comment line `: ping` so proxies do not close the stream. Comments are not events and have no id.
- `404` if the session id is unknown. `400` if `after` is not an integer.

`GET /api/v1/sessions/{id}/turn` returns the lock and pause snapshot in [api.md](api.md). The health strip and the session list use it. It does not stream.

### 3.2 Static files and the `--panel` flag

`AcpServer.Build` gains a `bool servePanel` argument, true only for `--panel`.

Order of middleware, first match wins:

1. `LoopbackGuardMiddleware` (always, including the extension's ACP server).
2. If `servePanel`: `UseDefaultFiles` and `UseStaticFiles` rooted at the panel `wwwroot`.
3. `AcpEndpoints.Map` (existing).
4. If `servePanel`: `PanelEndpointMapper` invoked from the CLI so it can close over `AcpSessionStore` and `PanelServices`.
5. If `servePanel`: fallback to `index.html` for any GET that is not under `/api/`. A missing asset returns the file's real 404, not the SPA. Only extensionless routes fall back, so a missing `app.js` is not served as HTML.

`--panel` in `Program.cs`:

- Sets `acp.Enabled = true` and takes the existing `--acp-only` branch (no TUI).
- Default port stays 7475. `--acp-port` still overrides.
- Does not set `BindAllInterfaces`. Docker's current `BindAllInterfaces = runningInDocker` is skipped when `--panel` is set. The panel binds loopback even inside a container. Publishing the container port onto `0.0.0.0` on the host is an operator choice in Compose, and LAN mode (section 7) is the only way the process itself listens beyond loopback.
- Writes or updates `~/.openmono/panel.json` with the chosen port. Mode `0600` on Unix. On Windows, an ACL that grants the current user only, as close as `File.SetUnixFileMode` allows; if the runtime cannot restrict the ACL, the file stays in the user profile and the log says so.
- Does not open a browser itself. The tray, the systemd unit, and the bash `panel` command do that, and only when stdout is a desktop session (`DISPLAY` or `WAYLAND_DISPLAY` on Linux, an interactive user on Windows, not an SSH session without those variables).

### 3.3 `openmono status --json`

`cmd_status` and `cmd_tunnel status` in `openmono` grow `--json`. They print one JSON object on stdout and no color. Exit 0 when the command could run, even if the model is down. The object's `model.state` carries `down`. Exit 2 when Docker or the CLI itself could not be queried. Fields match `PanelHealth` in [api.md](api.md) so the panel can shell out and deserialize, until a later change calls the same probes in-process. The first slice may probe `/health` and `/v1/models` in C# and shell out only for GPU and tunnel. It must not scrape ANSI.

### 3.4 What stays

- Route paths and JSON names already in `AcpEndpoints` and `AcpTurnRunner`. Listed in [api.md](api.md).
- Plan mode default on new sessions.
- `409 session_busy`.
- Host-bridge refusal of `--host` combined with `--acp-only`. `--panel` does not imply `--host`.
- Session JSONL under `~/.openmono/sessions/`.
- Permission decisions in `PermissionEngine` and `HostConsent`. The panel sends the strings those types already accept.

## 4. Allowlisted command wrapper

`PanelCommandRunner` is the only place the panel starts `openmono`. It does not take a shell string.

```
readonly record struct PanelCommand(
    string Name,
    string[] Args,
    bool Confirm,
    bool WindowsSupported,
    TimeSpan Timeout);
```

The dictionary is fixed in source. A request whose `command` is not a key returns `400` `{ "error": "unknown_command" }`. Arguments from the client are not appended to the process argument list. The few commands that need a choice (role, gpu) select a prebuilt argv from an enum, they do not interpolate strings.

| Name | Argv | Confirm | Windows |
|---|---|---|---|
| `setup-agent` | `openmono setup --agent` | no | no, the Windows installer is the exe, not this command |
| `setup-inference` | `openmono setup --inference` plus `--gpu` or `--cpu` from the detected enum, not from free text | no | no |
| `setup-full` | `openmono setup --full` with the same gpu enum | no | no |
| `start-inference` | `openmono start` | no | no |
| `stop-inference` | `openmono stop` | yes | no |
| `restart-inference` | `openmono restart` | yes | no |
| `upgrade-check` | `openmono upgrade --check` | no | no |
| `upgrade-apply` | `openmono upgrade` | yes, and `409` `{ "error": "session_busy" }` if any ACP turn holds the lock | no |
| `tunnel-start` | `openmono tunnel start` | no | no |
| `tunnel-stop` | `openmono tunnel stop` | yes | no |
| `rotate-key` | `openmono tunnel rotate-key` | yes | no |

Environment passed to the child, and nothing else from the panel process environment except `PATH`, `HOME`, `USERPROFILE`, `LANG`:

- `OPENMONO_NONINTERACTIVE=1`
- `OPENMONO_ROLE` set by the command, not by the client body
- `OPENMONO_GPU=1` or `OPENMONO_CPU=1` when the prebuilt argv says so
- `OPENMONO_HOST_BRIDGE` is not set by any MVP command. The host bridge stays off.

Working directory is the repository root the `openmono` bash script already uses (`REPO_DIR`), not the project folder. The project folder is the agent's `--workdir`, which is a different process.

One job at a time per machine. A second `POST /api/v1/panel/jobs` while one is running returns `409` `{ "error": "job_busy", "job_id" }`. Timeout is 15 minutes for setup (model download) and 2 minutes for start/stop/status. On timeout the child is killed and the job state is `failed` with `detail: "timed out"`.

Confirm flow for `Confirm == true`:

1. First POST without `confirm_token` returns `409` `{ "error": "needs_confirm", "confirm_token", "expires_at" }`. The token is 32 random bytes, hex, stored in memory, single use, 2 minutes.
2. Second POST with that token starts the job.
3. The SPA shows the command name in a sentence ("Stop the model on this computer?") and sends the second POST only from that button.

Stdout and stderr are decoded as UTF-8 with replacement, split on newlines, and appended to the job log with a `seq`. ANSI CSI sequences are stripped before storage. The log ring is 5,000 lines.

A setup script that asks for a reboot (NVIDIA driver) is detected by the existing installer message "reboot required" on stdout, or by exit code if the installer grows one. Job state becomes `needs_reboot`. The panel does not reboot the machine.

On Windows every row above returns `501` `{ "error": "not_on_windows", "detail": "The model runs on another computer." }` except the in-process operations in [api.md](api.md) (`model`, `project`, `agent` start/stop). The Windows panel does not look for a bash `openmono`.

Finding the CLI: `PanelCommandRunner` runs the `openmono` next to the published binary, then `~/openmono.ai/openmono`, then `PATH`. If none exist, jobs return `424` `{ "error": "cli_missing", "detail": "OpenMono is not installed on this computer." }`. The wizard then shows Install only where the platform can actually install (Linux and macOS). On Windows, Install is the installer that just ran; the page does not offer it again.

## 5. Agent-host pairing

Phase 2. Specified now so the connection screen's `mode` field does not change.

The agent-host panel and the agent panel are two processes. The browser on the laptop never reads the inference key. The agent panel's server does.

1. On the agent-host, `POST /api/v1/panel/pairing/codes` creates an 8-character code from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` (no 0/O, 1/I). Store SHA-256 of the code, the expiry (10 minutes), and single-use. The response includes the code once and the LAN endpoint `http://{best-lan-ipv4}:7474`. "Best" is the first non-loopback IPv4 that is up. If there is none, `endpoint` is null and the screen says to use the public relay or a typed address.
2. The person reads the code and the panel URL `http://{ip}:7475` on the agent-host screen and types both on the agent box.
3. The agent box `POST /api/v1/panel/connection` with `{ "mode": "pair", "host_panel": "http://192.168.1.20:7475", "code": "AB3K9Q2M" }`.
4. The agent process, not the browser, `GET`s `{host_panel}/api/v1/panel/pairing/codes/{code}`. That call is allowed from a non-loopback peer only when it presents the code in the path and the code matches a live hash. It does not require the panel bearer token, because the agent box does not have it yet. Rate limit: 5 failures per source IP per minute, then `429`.
5. One success returns `{ "endpoint", "api_key" }` and burns the hash. A second use is `404`.
6. The agent process writes `llm.endpoint` and `llm.api_key` through the same merge `ConfigLoader` uses for `~/.openmono/settings.json`, then probes `/v1/models`. The HTTP response to the browser is the `PanelModel` object, which has `api_key_set: true` and no key.

When the two panels cannot route to each other (relay, or a firewall):

- The agent-host screen offers Reveal once. `POST /api/v1/panel/connection-card/reveal` returns the inference key once to a loopback caller and sets `revealed: true`.
- The person types endpoint and key into the agent screen with `mode: "direct"`. The key is in the POST body and is not echoed.

Same-computer connection is `mode: "local"`. The server writes `http://127.0.0.1:{inference.port}` from settings, default `7474`, and does not ask for a key if `docker/.env` already has `LLAMA_API_KEY` and settings are in sync. If they differ, the probe failure `auth` is the screen state.

## 6. Packaging and launch

### Linux and macOS

`openmono panel` in the bash CLI:

- Resolves the published `openmono` .NET binary the same way `cmd_agent` does.
- Exec `openmono-bin --panel --acp-port "$PORT"`.
- After the process is listening (poll `GET /api/v1/panel/bootstrap` for up to 20 seconds), if this is a desktop session, open the URL once. Linux: `xdg-open`. macOS: `open`. Failure to open is a warning, not a failed start.
- Install the unit on `openmono setup` when the panel question is answered yes (default yes on a desktop, default no when `OPENMONO_NONINTERACTIVE=1` or no `DISPLAY`/`WAYLAND_DISPLAY`).

systemd user unit `~/.config/systemd/user/openmono-panel.service`:

```
[Service]
ExecStart=%h/openmono.ai/openmono panel --no-browser
Restart=on-failure
RestartSec=3
```

`--no-browser` is the flag for the unit and for SSH. `loginctl enable-linger` is documented, not forced. A server install uses a system unit only if the admin passes `--system-unit`, and the unit's `User=` is the account that owns `~/.openmono`, never root.

macOS LaunchAgent `~/Library/LaunchAgents/ai.openmono.panel.plist` runs the same command with `--no-browser` at load, and `openmono panel` from the installer opens the browser once.

### Windows

The published artifact is a `win-x64` folder or single-file exe built with `dotnet publish src/OpenMono.Cli/OpenMono.Cli.csproj -c Release -r win-x64`. It includes the panel `wwwroot`. `install.ps1` stays the from-source developer path and gains a note that the supported install is the published exe.

The installer:

- Copies the publish output to `%LOCALAPPDATA%\OpenMono`.
- Registers a per-user scheduled task `OpenMono Panel` at logon: `openmono.exe --panel --no-browser`. The task principal is the current user, not `SYSTEM`, and "run only when the user is logged on" so the tray can exist.
- Starts the process now and opens the browser.
- Adds a tray icon in the same process (a small `NotifyIcon` compiled only for `Windows`): Open, Start model is hidden, Quit. Quit cancels turns, then stops the host.

The panel process is the ACP server on Windows. Quit is the only stop for the process. "Stop" in the UI cancels the current turn and rejects new ones until Start. It does not exit the tray.

Updates on Windows, phase 2: download the published zip, verify a checksum published next to it, swap the folder, restart the task. Do not touch `%USERPROFILE%\.openmono\settings.json`. There is no git checkout to `openmono upgrade`.

### Port collision

If 7475 is taken, bind 7476, then 7477, up to 7485. Write the winner into `panel.json` and into `.openmono/agent.lock` `port`. The browser opens that URL. The extension's default is still 7475; the lock file is how it can follow a moved port later. The first slice does not change the extension. If 7475 is taken by an existing ACP server, the panel fails with a clear log line rather than silently becoming a second agent. Detect that by `GET /api/v1/discovery` on 7475: if `agent_id` is present, refuse to start and tell the person a panel or agent is already running.

## 7. Security controls the code must enforce

Implemented in `LoopbackGuardMiddleware` for every request, and in `PanelCommandRunner` for jobs. Tests are listed in section 8. The threat list is in the product design; this is the checklist.

1. Kestrel binds `ListenLocalhost` unless `panel.json` `bind` is `lan`.
2. `Host` must be `127.0.0.1`, `localhost`, or `[::1]`, with the actual port. Anything else is `421` with an empty body. In LAN mode the allowed hosts are those plus the configured names in `panel.json` `allowed_hosts`.
3. If `Origin` is present it must equal `http://127.0.0.1:{port}` or `http://localhost:{port}` (and `https` when TLS is on). Else `403` `{ "error": "bad_origin" }`.
4. If `Origin` is absent, the peer address must be loopback. Else `401`. This keeps the extension, curl, and the host bridge working on loopback, and rejects a LAN client that is not a browser and has no token.
5. Browser mutations (`POST`, `PUT`, `DELETE`) that carried an `Origin` must also send `X-OpenMono-Request: 1`. A cross-site form cannot set that header. Missing header is `403` `{ "error": "missing_request_header" }`.
6. LAN mode requires `Authorization: Bearer {token}` on every `/api/` request. Compare in constant time. The token is created when LAN mode is turned on, shown once in the rotate response, and stored as SHA-256 in `panel.json`. GET never returns the token.
7. `wwwroot` and `/api/v1/discovery` do not contain the token, the inference key, or provider keys.
8. Model and provider PUT accept a key and the following GET returns `api_key_set` only.
9. Job argv comes from the table in section 4. Tests pass a body with `args: { "extra": "rm -rf /" }` and assert the process argument list is the table's argv.
10. Confirm tokens are required for stop, restart, upgrade apply, tunnel stop, and rotate-key.
11. Upgrade apply is rejected while a turn is busy.
12. Pairing code lookup is rate limited and single use. The key is returned to the server-side caller, not embedded in HTML.
13. File reads, when they exist in phase 2, go through `PathGuard` rooted at the session workspace. The first slice has no file route, so there is nothing to forget.
14. The SPA stores nothing but UI preferences in `localStorage` (dismissed checklist, theme). No keys, no tokens.
15. Logs strip lines that match `Authorization:`, `api_key`, `Bearer `, and `LLAMA_API_KEY=` before they are stored or streamed. Reuse the idea of `LogRedactor` already used by the host bridge tests.

TLS for LAN mode is phase 2: a certificate generated into `~/.openmono/panel.pfx` on first LAN enable, Kestrel `ListenAnyIP` with that cert only. Until that task lands, the settings screen can save `bind: lan` as a stored intent but the running process keeps loopback and the screen says LAN access is not on yet. Do not listen on all interfaces in clear text in the first slice.

## 8. Testing

| Test | Where | Proves |
|---|---|---|
| Existing `AcpEndpointsTests`, `AcpTurnRunnerTests`, `SseWriterTests` | `OpenMono.Tests` | No header means the old SSE bytes and the old cancel-on-abort |
| New `TurnDetachTests` | `OpenMono.Tests` | With the header, disposing the HTTP body does not cancel; `abort: true` does; `GET events?after=` replays then receives a new `text_delta` |
| New `LoopbackGuardTests` | `OpenMono.Tests` using `WebApplicationFactory` or the existing endpoint harness | Bad `Host` is 421; bad `Origin` is 403; browser POST without the custom header is 403; loopback POST with no Origin still creates a session |
| New `PanelCommandRunnerTests` | `OpenMono.Tests` | Unknown command rejected; extra args ignored; confirm required; Windows returns `not_on_windows` for `start-inference`; ANSI stripped |
| New `PanelModelTests` | `OpenMono.Tests` | PUT with a key, GET without the key, file on disk contains the key |
| `scripts/acp-smoke.sh` | shell | Unchanged script still sees `event: done` |
| SPA unit tests | `client`, Vitest | Approval buttons emit the JSON in [api.md](api.md); wizard cannot finish while the model test is `auth` |
| One browser pass | manual or Playwright against a fake `ILlmClient` | Wizard, one chat turn, an approval card, a reload that shows the same turn still running |

No GPU, Docker, or live model in CI. Health probes use a stub `HttpMessageHandler`. Command tests use a fake process start that records argv.

The first code PR runs `dotnet test OpenMono.sln`. It does not need to run the SPA tests until `client/` exists; the PR that adds `client/` adds the Vitest run.

## 9. Tasks

Each task is one PR-sized change. Acceptance is the list under it. Do them in order inside a phase. Sizes are defined at the top of this file.

### Phase 1 — MVP

**1.1 Event log and detach. M.**

- `X-OpenMono-Detach: 1` keeps the turn alive after the response stream closes.
- Absent header preserves cancel-on-disconnect.
- `GET /api/v1/sessions/{id}/events` and `GET .../turn` match [api.md](api.md).
- `TurnDetachTests` and the old ACP tests pass.
- `acp-smoke.sh` passes without the new header.

**1.2 Loopback guard. S.**

- The five rules in section 7 that apply without LAN mode are enforced on `/api/v1/*`.
- Extension-style calls (no Origin, loopback peer) still create a session and run a turn.
- `LoopbackGuardTests` pass.

**1.3 Panel library and `--panel`. M.**

- `src/cc/OpenMono.Panel` exists, is in the solution, and references only `OpenMono.Core`.
- `openmono panel` and `openmono.exe --panel` serve `index.html` and `GET /api/v1/panel/bootstrap`.
- `dotnet build OpenMono.sln` and `dotnet test OpenMono.sln` pass.
- No second listener on 7475: if discovery is already up, the process exits with the message in section 6.

**1.4 Model, project, and health. M.**

- `GET/PUT /api/v1/panel/model`, `POST .../model/test`, `GET/PUT /api/v1/panel/project`, `GET /api/v1/panel/health` match [api.md](api.md).
- Keys are write-only.
- Project PUT rejects the home directory and the filesystem root unless `confirm_root` is true.
- Health uses a stub in tests and a real HTTP call only against the configured endpoint.

**1.5 Jobs for the machines that have the CLI. M.**

- The command table in section 4 is the allowlist.
- Setup, start, stop, and upgrade-check stream logs on `GET /api/v1/panel/jobs/{id}/logs`.
- Windows returns `not_on_windows` for those commands.
- A reboot line in stdout sets `needs_reboot`.

**1.6 SPA: shell, wizard, connect, project, health, logs. M.**

- Screens and states in [screens.md](screens.md) for the wizard, connect, project picker, health strip, install log, and logs exist.
- The checklist blocks the chat route until the wizard is complete.
- `client/src/api` is the only `fetch` site. A unit test fails the build if another file imports `fetch`.

**1.7 SPA: chat, approvals, diffs, sessions. M.**

- A detached `EventSource` renders `text_delta`, tool events, and the approval cards.
- Reload mid-turn resumes from `Last-Event-ID` and the turn is still running (covered by the browser pass in section 8).
- Approval POSTs use the existing ACP bodies. Diffs call the existing diff routes.
- Stop sends `{"abort":true}`.

**1.8 Desktop launch. M.**

- Linux user unit and macOS LaunchAgent files are written by setup, with `--no-browser`.
- Windows scheduled task and tray match section 6.
- A desktop install opens the browser once. An SSH or service start does not.

Phase 1 is done when the acceptance lists above are all true and the product-design MVP sentence holds: open the browser, point at the model, pick a folder, chat, with start, stop, health, and logs on the page.

### Phase 2 — two machines

**2.1 `status --json` and `tunnel status --json`. S.** Health prefers the JSON over ad hoc probes.

**2.2 Pairing and the reveal-once card. M.** Section 5, including the rate limit test.

**2.3 Upgrade Apply, disabled while a turn is busy. S.**

**2.4 Session list "waiting for you", playbook list, read-only file tree. M.** Routes in [api.md](api.md). File routes use `PathGuard`.

**2.5 LAN bind with TLS and a bearer token. M.** Until this lands, the bind setting does not open a clear-text port.

### Phase 3 — work that outlives one tab

These wait on `AgentSession` and `openmono agent --headless` from the unmerged framework plan (PR #166) where noted.

**3.1 Queue in front of the turn lock. M.** A message sent while busy is stored and started when the lock frees, instead of `409`. Abort clears the queue. Acceptance: two rapid POSTs with detach both run, in order, and the extension's no-detach `409` behavior stays.

**3.2 A schedule that runs a playbook or a headless task. L.** One clock in the panel process. Writes the usual JSONL. No webhook receiver.

**3.3 More than one project. L.** `openmono panel` supervises one child ACP process per folder on its own port and proxies `/api`. This is the only companion process, and it is the same binary.

**3.4 Structured sub-agent events. S.** Add optional `agent` and `status` fields on `sub_agent_log` without removing `line`.

## 10. Risks

| Risk | What to watch | Mitigation in the tasks |
|---|---|---|
| Detach changes the extension | A client that drops the socket and expects the turn to stop | Opt-in header. Old tests stay. Smoke script stays header-less |
| CLI dependency cycle | Panel library needs `AcpSession` | Session types stay in the CLI. The library takes `AppConfig` and delegates, it does not reference `OpenMono.Cli` |
| Setup scripts are interactive | `OPENMONO_NONINTERACTIVE=1` still prompts on some paths | Job log shows the prompt. The task is failed, not hung, because of the timeout. A follow-up makes that prompt honor the env flag. Do not block the chat MVP on a fully silent NVIDIA install |
| Self-upgrade kills the page | `openmono upgrade` rebuilds and the unit restarts | Phase 2 Apply is disabled during a turn. The SPA polls bootstrap until `revision` changes |
| Windows has no `openmono` bash | Someone wires the Linux job table into the exe | `WindowsSupported` is false for those rows and tested |
| `wwwroot` missing in publish | The exe starts and every route is the API only | Publish test asserts `index.html` is next to the binary. The process logs "panel UI missing" and still serves `/api` so the extension is not broken |
| Reboot during driver install | The unit does not come back | `needs_reboot` state. The installer text already tells the person to run setup again. The unit is enabled before the reboot |
| Pairing code guessed | 8 characters from 32 symbols is not a password for the whole machine | Single use, 10 minutes, rate limit, and the response is the inference key only, not a panel admin token. LAN mode still has its own bearer |

## 11. Open questions

1. Should `GET .../events` be the only stream, with `POST .../turn` returning `202` and a seq, even for detach clients? Recommendation: keep SSE on the POST as well, so a client that can hold the connection still sees tokens immediately, and GET is the resume path. Both write the same log.
2. Where does the Windows tray live if WinForms inside the ASP.NET exe is awkward? Recommendation: the same exe, `#if WINDOWS`, `NotifyIcon`. A second tray exe is a second updater and is not worth it.
3. Does `openmono panel` on a full install also `openmono start` when `/health` is down? Recommendation: yes, once, on panel start, matching `cmd_agent_host` which already starts local inference when the endpoint is loopback and down. Failure is a red health strip, not a failed panel.
4. Is the SPA's router hash-based (`/#/chat`) or path-based? Recommendation: path-based with the Kestrel fallback in section 3.2, so refresh on `/chat` returns `index.html`.
5. PR #166 is unmerged. Phase 3 names it as a dependency. Phases 1 and 2 do not.
