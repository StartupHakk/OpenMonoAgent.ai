# bot/: the OMA bot roster (oma-bot)

Several named OMA bots, each in its own container with its own personality, workspace, data dir, port and routines, led by a lead bot called head-honcho, with a separate read-only health bot. Inference is always remote, through the roster gateway and the FRPS and FRPC relay. Everything here is an add-on: no OMA file outside `bot/` is changed.

- Plan: [docs/OMA-BOT-ROSTER-PLAN.md](docs/OMA-BOT-ROSTER-PLAN.md) (the plan wins wherever other notes disagree)
- Builder decisions waiting for Spencer: [docs/decisions.md](docs/decisions.md)
- Build progress per phase: [docs/PROGRESS.md](docs/PROGRESS.md)
- Phase 0 record: [docs/phase0/](docs/phase0/)

License: AGPL-3.0, like the rest of OMA.

## Layout

| Path | What |
|---|---|
| src/Omabot.Core | Shared library: team file, roster.db, rendering, container specs, ACP client, supervision, gateway, doctor, CLI commands |
| src/Omabot | The `oma-bot` CLI and service (`oma-bot serve`) |
| src/Omabot.Supervisor | The only process that talks to Docker; serves `~/.omabot/run/supervisor.sock` (0600, bearer token) |
| src/Omabot.EgressGate, src/Omabot.NodeAgent, mcp-shim | Later phases (egress gate, fleet node agent, MCP shim) |
| src/Omabot.Tests, src/Omabot.SecurityTests | Unit, integration, regression and security tests named by plan test id |
| templates | Hard rules, prompt templates, permission profiles, default team file (embedded in Omabot.Core) |
| hooks | Bash hooks mounted read only into every bot (pre tool check, input blocks, post write flag) |
| docker | Example compose file that mirrors the rendered container spec |
| install.sh, uninstall.sh | Per user install, no sudo |

## Build and test

```bash
export PATH=$HOME/.dotnet:$PATH     # .NET 10 SDK
cd bot
dotnet build Omabot.sln
dotnet test Omabot.sln
```

Tests that need Docker, the agent image, a GPU, the relay or a second LAN host are marked skipped with the reason in their name list.

## Install and first run

```bash
./install.sh                 # or ./install.sh --systemd to write user units
oma-bot init                 # ~/.omabot, roster.json, default team file
# set gateway.upstream in ~/.omabot/roster.json to the FRP relay endpoint (https, see decision B1)
oma-bot secrets set inference < /path/to/keyfile   # stored 0600, never on the command line
oma-bot team apply ~/.omabot/team/team.yaml
oma-bot doctor
oma-bot-supervisor &         # or the systemd user unit; needs Docker access
oma-bot serve &              # gateway and health loop
oma-bot up
oma-bot run head-honcho "Hello, who are you?"
oma-bot gateway slots        # what the inference server reports
oma-bot gateway bench --save # measure and record capacity (task C1)
oma-bot usage                # calls and tokens per bot and routine
```

More: [docs/gateway.md](docs/gateway.md), [docs/secrets.md](docs/secrets.md), [docs/memory.md](docs/memory.md).

Memory: `oma-bot memory show BOT`, `oma-bot memory journal BOT`, `oma-bot memory undo BOT ID`, `oma-bot memory forget BOT "fact"`.

Exit codes: `team apply` 0 ok, 1 refused, 2 invalid file, `--check` 6 when something would change; `run` 0 ok, 3 needs input, 4 failed, 5 timed out; `doctor` 0 ok, 1 problems.

## Safety in one screen

- No bot approves its own permission requests; approvals come only from the roster broker (phase 6, tests SEC-27 to SEC-31).
- Bots never hold the real inference key. Each bot has a per bot gateway token; the gateway holds the key.
- The supervisor is the only holder of docker.sock. Container specs are checked for safety in the launcher, the supervisor and the engine client: cap drop ALL, no-new-privileges, non root, ACP port on 127.0.0.1 only, host-bridge.json shadowed by /dev/null, logs on tmpfs, never docker.sock.
- Hard rules live in the data dir system prompt, outside the workspace. Writes to OPENMONO.md, .openmono, memory files and key files are denied.
- Secrets are redacted in logs and memory; `oma-bot doctor` fails when a secret shows up in a log or memory file.
- One host by default. Fleet mode is optional and only on Spencer's direction.
