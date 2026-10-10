# Deployment modes - single-box vs split-box

This page makes the two supported deployment shapes explicit: **where
inference lives** vs **where the agent and its tools live**, how to install
each shape, and how to do real server work (pull a repo, deploy, troubleshoot
a backend) once installed.

| | **Single-box** | **Split-box (direct)** | **Split-box (relay)** |
|---|---|---|---|
| Inference (llama-server + model) | Same machine as the agent | A different machine on your network/VPN | A different machine, reached via relay |
| Agent + tools (CLI, Bash, git, docker) | Same machine | The box you are working on | The box you are working on |
| Needs hosted relay account | No | No | Yes (`app.openmonoagent.ai`) |
| Best for | One server that does everything | LAN/VPN or SSH between your boxes | Agent laptop + remote GPU over the internet |

Existing install paths are unchanged: `openmono setup` still prompts for the
same three roles (`full` / `inference` / `agent`). This document only explains
how to combine them into each shape.

## Agent modes: host vs sandbox (pick one per run)

Wherever an agent runs below, it runs in one of two modes. Inference
(`llm.endpoint` = local or relay) works identically in both.

| | `--sandbox` (default) | `--host` (server sub-agent) |
|---|---|---|
| What runs | Agent in a Docker container, project bind-mounted as `/workspace` | .NET bare-metal sub-agent (`src/OpenMono.HostBridge`) that drives this box's in-container agent over ACP and executes host commands itself |
| Model loop | In the container | In the container (unchanged) - the bridge never runs inference |
| Inference | `llm.endpoint` - local (`localhost:7474`) or relay | `llm.endpoint` - local or relay (remote, like agent-only) |
| VS Code / Cursor extension (`--acp-only`) | **Yes - sandbox only, by design** | No - rejected with an error directing you to sandbox mode |
| Best for | Contained coding work; the only mode the editor extension drives | Operating the machine itself: pull, build, install, redeploy, troubleshoot |

```bash
openmono agent --sandbox   # container (default when no flag is given)
openmono agent --host      # server sub-agent: container loop + bare-metal executor
OPENMONO_AGENT_MODE=host openmono agent   # same thing via env
```

### How the bridge talks ACP (no protocol changes)

The bridge is an ACP **client** against the stock container API - the same
endpoints the VS Code extension and `scripts/acp-smoke.sh` use:

- `GET /api/v1/discovery` (wait until ready), `POST /api/v1/sessions`,
  `POST /api/v1/sessions/{id}/turn` with `{"message": ...}` (SSE stream).
- Pauses resolve with fresh POSTs: `permission` (`allow`/`deny` + scope),
  `user_input` (answers **and** host-command output), `playbookPermission`,
  `toggle_mode`. SSE events rendered: `text_delta`, `tool_start`/`tool_end`,
  `usage`, `done`, `error`.
- Host execution rides the existing `AskUser` → `user_input_request` channel:
  the agent asks `HOST_EXEC: {"command": "...", "timeout_ms": N,
  "background": false}`; the bridge allow-list checks it, runs it on bare
  metal, and resumes the turn with the output. No new endpoint, no new pause
  kind, no container or extension changes.

Project layout (`src/OpenMono.HostBridge/`, .NET 10, references the CLI project for its TUI):

| File | Responsibility |
|---|---|
| `Program.cs` / `BridgeOptions.cs` | CLI (`--task`, `--session`, `--acp`, `--port`, `--keep`, `--non-interactive`, `--tui`, `--classic`, `--plan`, …), TUI/line loops, discovery wait, mode ensure |
| `AcpClient.cs` | HTTP + SSE client for the stock ACP API (+ `{"mode":…}` ensure) |
| `AgentContainer.cs` | Spawns the unmodified `agent` service detached (`--acp-only`, loopback port publish, git passthrough, endpoint mapping), stops what it started |
| `HostExecutor.cs` | `HOST_EXEC` envelope parse, allow → deny → default policy, foreground/background bare-metal execution, sudo via run-as identity, audit |
| `HostIdentity.cs` | Run-as identity: username (config) + password (memory-only, zeroed on dispose; `--password-file` / `OPENMONO_HOST_PASSWORD_FILE`) |
| `AuditLog.cs` / `LogRedactor` | Paper trail: every host command appended to the existing agent log |
| `Bridge.cs` | Turn pump: renders stream, auto-resolves policy pauses, prompts the operator otherwise |
| `BridgeUi.cs` / `TuiFrontend.cs` | Operator surface: full-screen agent TUI on a TTY, plain lines otherwise |
| `OperatorPreamble.cs` | Standing orders prepended to the first turn of fresh sessions |
| `BridgeConfig.cs` | `~/.openmono/host-bridge.json` (tool + host-exec policy, timeouts, log dir, `run_as`, `allow_sudo`; 0600, never holds secrets) |

### Host session behavior (what the operator sees)

- **Full TUI.** On a terminal, `openmono agent --host` opens the same full-screen
  agent TUI as `openmono agent` (streaming, tool cards, thinking, permission
  menus, PLAN/BUILD chrome, `/quit`, `/clear`). `--classic` forces the old plain
  line interface; piped/non-interactive runs stay line-oriented. Slash commands
  (`/mode`, `/build`, `/plan`, `/compact`, `/think`, `/playbook`) run on the
  server; only `/quit`, `/exit`, `/clear` are local.
- **Build mode by default.** Fresh and attached host sessions are explicitly set
  to `build` via the stock `{"mode": …}` turn - new ACP sessions default to plan
  (read-only), which cannot operate a server. `--plan` opts back into read-only.
  A build-toggle request while already in build is approved without prompting.
- **Standing orders.** The first turn of every fresh host session carries a
  preamble: container tools see only the workspace; every host action (shell,
  files, logs, git, docker, systemctl, installs) goes through AskUser +
  `HOST_EXEC` as the run-as user; logs live at `~/.openmono/logs` on the host
  and are read only via `HOST_EXEC` (never container Grep/FileRead on
  `/root/...` or `~/.openmono/...`). Attached (`--session`) conversations are
  never re-preambled.
- **Wrong-user fail-fast.** If `run_as` differs from the bridge process user and
  sudo is off, the bridge exits immediately with how to fix it (run as that user
  or enable sudo) instead of failing command by command mid-session.

### Failure modes walked

| Symptom | Cause | Fix in this design |
|---|---|---|
| Agent Greps `/root/.openmono/...` or `~/.openmono/...` and fails | Container tools cannot see host paths; agent guessed | Preamble + playbook route all host reads through `HOST_EXEC`; logs documented at the run-as user's `~/.openmono/logs` |
| y/N prompt for every host command | Stock policy defaults to `ask` (every command prompts) | Expected in ask mode; the allow list never skips the prompt. Opt in to `OPENMONO_HOST_EXEC_DEFAULT=allow` at install for routine commands to run without asking; deny list + sudo rules still enforced, all commands audited |
| Launch line becomes the first task | Stale keystrokes in the console buffer at TUI startup | Stdin drained on entry; `openmono agent --host`-shaped first lines are never sent |
| Big HOST_EXEC stalls on PENDING_RESPONSE with no prompt | Model sends envelope JSON with literal newlines (invalid JSON); bridge parked the operator in front of raw JSON | Lenient parse accepts raw heredocs (truncation fails closed); unparseable envelopes get an auto-reply with re-send shapes instead of waiting on the operator |
| Pasted paragraph splits into queued lines, tail dropped | Unbracketed paste submits per line; queue capped at 2 | Queue holds 32; the bridge joins everything pending into one turn |
| Turn error 400 "must contain either 'content' or 'tool_calls'" | A turn that produced no text and no calls stored an empty assistant message, rejected on the next request | Empty assistant messages are never stored (warn-logged); the per-request sanitizer drops any already in history, so old sessions heal |
| Agent stays in Plan, proposes but never acts | New ACP sessions default to plan | Bridge sets `build` explicitly unless `--plan` |
| Host commands fail as the wrong user | Bridge run as root (or another user) while `run_as` is the login user | Fail-fast startup check; run the bridge as the login user |
| `as_root` refused | Sudo never opted in | Expected: enable via `--init`/first-run prompt, or keep off and do root steps by hand |
| Sudo password prompt mid-turn | Timestamp expired and no password in memory | Masked prompt (TUI stands down fullscreen so the secret never touches its input box), or pre-seed `--password-file` / `OPENMONO_HOST_PASSWORD_FILE` |
| VS Code driving the host | Extension integration is sandbox-only by design | `openmono agent --host --acp-only …` is refused; run without `--host` for the extension |

### Run-as identity, sudo, and the paper trail

- **Identity.** Host commands run as `run_as` from `~/.openmono/host-bridge.json`
  (empty = whoever runs the bridge). Set it at install, with `host-bridge --init`,
  `--run-as <user>`, or `OPENMONO_HOST_RUN_AS`. The password for that account is
  **never** stored in settings, logs, or the repo: it lives only in bridge memory
  (masked prompt when an escalation first needs it, zeroed on exit), optionally
  pre-seeded from a root-owned-or-owner-only `0600` file via `--password-file` /
  `OPENMONO_HOST_PASSWORD_FILE` (loose permissions are refused). It travels to
  sudo over piped stdin, never a command line.
- **Sudo.** The installer / `--init` / first run asks once whether sudo is allowed
  (default **No**); the answer persists as `allow_sudo`. `false` means sudo is never
  used: `as_root` requests, cross-user runs, and any command segment whose
  program is `sudo`, `su`, `doas`, `pkexec`, or `run0` (including
  path-prefixed, backslash-escaped, or quoted forms such as `/usr/bin/sudo`
  or `\sudo`) are refused with how to enable it.
  `--allow-sudo` / `--no-sudo` override per run (explicit, never silent).
  `run_as` is validated against `^[a-z_][a-z0-9_-]*[$]?$` before use in `sudo -u`.
- **Timeouts.** A timed-out foreground command is killed (whole process tree;
  via passwordless sudo when the command ran elevated) and the report says
  `was terminated` only when the process is confirmed gone. Otherwise it says
  the timeout fired but the process may still be running, with the pid.
- **Host policy (ask by default).** `host_exec.default` ships as `ask`: every
  host command prompts with y/N. The allow list does not skip the prompt in
  ask mode. Set `host_exec.default` to `"allow"` only by explicit opt-in
  (`OPENMONO_HOST_EXEC_DEFAULT=allow` at install) to run routine commands
  without asking. In allow mode an allow pattern only matches a single simple
  command: any command containing `; & |` backtick `$(` `${` `>` `<` or a
  newline never allow-matches and falls to the default (usually ask). The
  shipped allow sample is `git *`, `systemctl status *`, `journalctl *`
  (`docker *` and `curl *` were removed as too broad). The deny list is
  checked first, per command segment (split on `; & |` and newlines), with the
  program word normalized so `/bin/rm`, `/usr/bin/rm`, and `\rm` match `rm`
  patterns, plus an explicit guard for `rm` with recursive+force flags
  targeting `/` or `~`.
- **Deny list is best-effort, not a sandbox.** It blocks known destructive
  shapes and common obfuscations (path-prefixed programs, chained segments,
  `rm` flag variants), but shell is expressive enough that a determined
  prompt can work around any pattern list. Treat the deny list as a guardrail
  against mistakes, not as a security boundary. The only real isolation
  is running the agent sandboxed (`openmono agent --sandbox`) or reviewing
  each host command in ask mode.
- **Paper trail.** Every host command - runs **and** denials - is appended to the
  **existing** agent log (`~/.openmono/logs/openmono-<date>.log`, same file and line
  format the container agent writes through its mount), tagged `[host-exec]` with
  command, user it ran as, `as_root`, exit code, elapsed ms, and timestamp. Command
  output is **not** logged (it may carry secrets; the operator still sees it live).
  A conservative redactor strips `password=`/`api_key:`/`Bearer …`/`--password …`
  shapes from anything that reaches the log. Passwords are never logged.

### Installing the sub-agent (opt-in add-on, three roles unchanged)

`full`, `inference`, and `agent-only` work exactly as before. On `full` and
`agent-only` installs, setup asks once (default **No**) whether to add the
server sub-agent; `inference`-only never offers it:

```bash
openmono setup --agent     # asks: Install the host sub-agent? [y/N]
OPENMONO_HOST_BRIDGE=1 openmono setup --agent   # non-interactive: include it
```

Answering yes runs `scripts/install-host-bridge.sh`, which publishes
`src/OpenMono.HostBridge` to `~/.openmono/bin/host-bridge`. .NET 10 is
required: when it is missing, the script reuses the **existing** prerequisite
path (`scripts/install_prereqs.sh` - the same installer that provides .NET 10
for the main agent) instead of inventing a second one, then publishes. Add it
to any existing agent box later with `bash scripts/install-host-bridge.sh`.

## Mode A - single-box (inference + agent on one machine)

Use this when one server (or workstation) has the whole stack: it runs the
model **and** the agent you operate software with.

### Install

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

When prompted, pick **1 - Both** (agent + inference server). Non-interactive
equivalent:

```bash
openmono setup --full        # auto-detects GPU vs CPU
openmono setup --full --gpu  # force NVIDIA GPU mode
openmono setup --full --cpu  # force CPU mode
```

### Verify

```bash
openmono status              # containers + llama-server health + loaded model
curl -sf http://localhost:7474/health
```

`~/.openmono/settings.json` on this box points at local inference (see
`config-examples/single-box.settings.json`):

```jsonc
{
  "llm": {
    "endpoint": "http://localhost:7474",
    "api_key": ""
  }
}
```

An empty `api_key` is fine here: the loopback endpoint is trusted the same way
as before this change. Set one only if you also expose the server (see Mode B).

### Run the agent and do server work

```bash
cd /srv/my-backend        # or wherever the repo lives
openmono agent --host     # sub-agent: container loop + bare-metal host commands
openmono agent            # sandbox: acts on the bind-mounted /workspace only
```

Server work uses the existing `Bash` tool inside the container plus `HOST_EXEC`
bare-metal commands when the sub-agent is attached (`git`, `docker`/`docker
compose`, `systemctl`, `journalctl`, `curl`). Host commands run on the machine
itself; sandbox `Bash` runs inside the container (host daemons reachable via
the mounted docker socket, host paths outside the mount unreachable by
design):

```
Clone https://github.com/<org>/<repo> into /workspace if it is empty,
otherwise git pull the default branch.
```

```
Deploy with docker compose up -d --build, then show `docker ps`
and the last 50 lines of the backend logs.
```

```
The backend returns 500 on /health. Inspect the logs, find the failing
commit or config, fix it, redeploy, and verify /health returns 200.
```

What makes this work on a server box (sandbox notes; host mode uses your
native git/docker/shell directly, no passthrough needed):

- **Git auth passthrough (sandbox)** - `openmono agent` forwards your host
  `~/.gitconfig`, `~/.ssh`, `~/.git-credentials` (read-only) plus the SSH
  agent socket, so the agent can clone/pull/push as you. Opt out with
  `OPENMONO_NO_GIT_AUTH=1`.
- **Docker socket (sandbox, opt-in only)** - the default `openmono agent`
  sandbox does NOT get the host daemon. Set `OPENMONO_DOCKER_SOCK=1` when the
  task actually needs `docker` against the host (deploy + troubleshoot a
  containerized backend), and the agent
  image ships the Docker CLI + compose plugin, so `docker ps`,
  `docker compose up -d --build`, and `docker logs` work from inside the
  agent when the socket is opted in. `OPENMONO_NO_DOCKER_SOCK=1` remains
  supported as an explicit veto for existing configs. If you invoke compose
  directly instead of via `openmono agent`, add
  `- /var/run/docker.sock:/var/run/docker.sock` to the agent service volumes
  yourself.
- **Workspace scoping** - the agent sees only the mounted project
  (`/workspace`). Point `WORKSPACE=/srv/my-backend openmono agent` at the repo
  you want it to operate on. Host paths outside the mount are unreachable by
  design; run host-level commands (`systemctl`, firewall) yourself or via your
  own shell, not the agent.
- **Long-running backends** - ask the agent to start servers with
  `background=true` (`Bash` tool flag); it returns a PID + log path you can
  `tail` on follow-up turns.

> Tip: `Bash` prompts before running commands by default. To reduce prompting
> for routine server work, pre-approve patterns in `settings.json` (see
> [CONFIG.md](CONFIG.md#permissions)):
>
> ```jsonc
> {
>   "permissions": {
>     "tools": {
>       "Bash": {
>         "allow": ["git *", "docker ps *", "docker logs *", "docker compose ps *"],
>         "ask": ["sudo *", "docker *", "systemctl *"],
>         "deny": ["rm -rf *"]
>       }
>     }
>   }
> }
> ```

## Mode B - split-box (inference remote, agent on your box)

Use this when the model runs somewhere else (GPU server) and the agent runs on
the box you are working on (server, laptop, or workstation). The agent still
does the same server work as Mode A - only `llm.endpoint` points at a remote
address. Two transports are supported; pick one.

### Option B1 - direct connection (no relay, no account)

Best for machines on the same LAN, VPN, or Tailscale-style network.

**On the inference box:**

```bash
openmono setup --inference
openmono start
openmono status   # confirm llama-server healthy + model loaded
```

If `docker/.env` has no `LLAMA_API_KEY` yet, generate one and restart so the
exposed endpoint is authenticated:

```bash
grep LLAMA_API_KEY docker/.env || openssl rand -hex 24
# if empty, add LLAMA_API_KEY=<generated> to docker/.env, then:
openmono restart
```

Allow inbound TCP to the llama port (default `7474`) from the agent box only,
e.g. with `ufw`:

```bash
sudo ufw allow from <AGENT_BOX_IP> to any port 7474 proto tcp
```

If you also installed the Caddy gateway (`openmono setup gateway|search|scraper`),
open that port instead (default `47480`) - the gateway fronts llama plus any
web services, and the agent auto-detects them via `/services`.

**On the agent box:**

```bash
openmono setup --agent
openmono config set llm.endpoint http://<INFERENCE_HOST>:7474
openmono config set llm.api_key <LLAMA_API_KEY>
```

(`config-examples/split-agent-box.settings.json` shows the same two keys as a
file. `<INFERENCE_HOST>` is the inference box's LAN/VPN address;
`<LLAMA_API_KEY>` is the value of `LLAMA_API_KEY` in the inference box's
`docker/.env`. Both are placeholders - no real secrets are checked in.)

Verify from the agent box before starting the agent:

```bash
curl -sf http://<INFERENCE_HOST>:7474/health
openmono config get llm.endpoint
```

Then work as in Mode A (either agent mode):

```bash
cd /srv/my-backend
openmono agent --host   # sub-agent: container loop, bare-metal host commands
openmono agent          # or sandboxed
```

**SSH tunnel alternative** (encrypted, no open firewall ports): from the agent
box, forward the remote llama port to localhost and keep `llm.endpoint` local:

```bash
ssh -N -L 7474:localhost:7474 <user>@<INFERENCE_HOST>
openmono config set llm.endpoint http://localhost:7474
openmono config set llm.api_key <LLAMA_API_KEY>
```

### Option B2 - relay (internet, no port forwarding)

Best for an agent laptop + remote GPU box over the internet. This is the
pre-existing hosted path and is unchanged:

1. Inference box: `openmono setup --inference`, `openmono start`,
   `openmono tunnel setup`, `openmono tunnel start`.
2. Agent box: `openmono setup --agent`, then `openmono config set llm.endpoint`
   + `llm.api_key` with the relay endpoint/key from the setup email.

Full steps: [SETUP.md - Dual-box setup](SETUP.md#dual-box-setup).

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `401 Unauthorized` from the agent box | `llm.api_key` differs from inference box `LLAMA_API_KEY` | Compare `openmono config get llm.api_key` (agent) with `grep LLAMA_API_KEY docker/.env` (inference); copy the inference value over |
| Endpoint unreachable / timeout | Firewall, wrong host/port, or llama still loading | `openmono status` on the inference box; `curl -sf http://<INFERENCE_HOST>:7474/health` from the agent box; check `ufw`/security-group rules |
| Agent `docker: command not found` (sandbox) | Stale agent image (built before the Docker CLI was added) | Rebuild: `cd docker && docker compose build agent` |
| `openmono agent --host`: "Host sub-agent is not installed" | `~/.openmono/bin/host-bridge` missing and no .NET SDK to run from source | `bash scripts/install-host-bridge.sh` (installs .NET 10 via existing prereqs when missing), or use `--sandbox` |
| `openmono agent --host --acp-only ...` refused | VS Code / extension integration is sandbox-only by design | Run without `--host`: `openmono agent --acp-only --acp-port 7475` |
| Agent `Cannot connect to the Docker daemon` | Socket not mounted (opt-in only) | Set `OPENMONO_DOCKER_SOCK=1` and retry via `openmono agent`; for direct compose use, add the socket volume (see Mode A) |
| Agent prompts on every git/docker command | Default `Bash` permission level is `Ask` | Add `allow` patterns as in the Mode A tip, or approve per-session when prompted |
| Model slow on single-box CPU | CPU inference needs ~20 GB RAM and dual-channel DDR5 | See [MODELS.md](MODELS.md); consider split-box with a GPU inference host |

## Follow-ups (intentionally out of scope)

- The bridge is implemented but not yet compiled or run end to end here (this
  environment has no .NET SDK or Docker daemon): needs `dotnet build`, a real
  `dotnet publish` smoke run, plus one relay-backed operator session before
  any PR.
- VS Code / Cursor extension stays sandbox-only and is untouched; no ACP
  server changes were made (`--acp-only` is rejected with `--host`).

- `openmono status` / `scripts/healthcheck.sh` still probe `localhost:7474`;
  remote-endpoint health checks are a manual `curl` for now.
- No `systemd` unit for the agent itself; run `openmono agent` in `tmux`
  (or equivalent) on servers.
- The `server-deploy` playbook example is a minimal starting point; canned
  rollback/verify scripts per stack (compose, systemd, k8s) are future work.
