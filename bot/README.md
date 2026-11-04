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
| src/Omabot.EgressGate | The egress gate: the only way out for bots and desktops (phase 7) |
| mcp-shim | The roster MCP shim inside each bot (roster tools, and the desktop bridge) |
| src/Omabot.NodeAgent | Fleet node agent `oma-bot-node` (phase 12, optional; `install.sh --node`) |
| src/Omabot.Tests, src/Omabot.SecurityTests | Unit, integration, regression and security tests named by plan test id |
| templates | Hard rules, prompt templates, permission profiles, default team file (embedded in Omabot.Core) |
| hooks | Bash hooks mounted read only into every bot (pre tool check, turn gate, input blocks, post write flag) |
| checks | Scripts routine command checks may run, always inside a throwaway container |
| docker | Example compose file that mirrors the rendered container spec, the egress gate and desktop images, and the HTTPS front recipe (web-front) |
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
oma-bot egress rules > ~/.omabot/firewall.sh && sudo sh ~/.omabot/firewall.sh   # roster firewall rules (docs/egress.md)
oma-bot doctor
oma-bot-supervisor &         # or the systemd user unit; needs Docker access
oma-bot images accept        # after checking the agent image; no bot starts before this
oma-bot serve &              # gateway, health loop, scheduler, broker, rooms, web view
oma-bot up
oma-bot run head-honcho "Hello, who are you?"
oma-bot web open             # one-time sign-in link for the local web view
oma-bot room post planning "@Builder what is left?"   # once the team file has a room (docs/rooms.md)
oma-bot gateway slots        # what the inference server reports
oma-bot gateway bench --save # measure and record capacity (task C1)
oma-bot gateway toolbench    # the 40 tool tasks, valid call rate before and after repair
oma-bot usage                # calls and tokens per bot and routine
```

Start here: [docs/install.md](docs/install.md), [docs/team-file.md](docs/team-file.md), [docs/cli.md](docs/cli.md), [docs/security.md](docs/security.md), [docs/capacity.md](docs/capacity.md), [docs/release-checklist.md](docs/release-checklist.md).

More: [docs/gateway.md](docs/gateway.md), [docs/secrets.md](docs/secrets.md), [docs/memory.md](docs/memory.md), [docs/routines.md](docs/routines.md), [docs/handoffs.md](docs/handoffs.md), [docs/approvals.md](docs/approvals.md), [docs/egress.md](docs/egress.md), [docs/desktops.md](docs/desktops.md), [docs/local-models.md](docs/local-models.md), [docs/rooms.md](docs/rooms.md), [docs/web.md](docs/web.md), [docs/multi-user.md](docs/multi-user.md), [docs/fleet.md](docs/fleet.md).

Handoffs: bots hand each other work with the roster tools (assign, consult, notify, ask, answer, cancel). `oma-bot assignments` lists live work, `oma-bot audit tree ROOT` replays a tree, `oma-bot run BOT "msg" --wait-done 600` follows a request until the work it started is done.

Memory: `oma-bot memory show BOT`, `oma-bot memory journal BOT`, `oma-bot memory undo BOT ID`, `oma-bot memory forget BOT "fact"`.

Routines: `oma-bot routines list`, `oma-bot routines runs BOT`, `oma-bot routines run-now BOT ROUTINE`, `oma-bot routines pause|resume BOT ROUTINE`. Changes a bot asks for wait in `oma-bot approvals` until a person answers.

Approvals: every pause in a roster turn goes to the broker. `oma-bot approvals` lists cards with the time left, `oma-bot approvals show ID` shows the exact action, `oma-bot approvals --answer ID allow|deny [--reason TEXT] [--text ANSWER]` answers; `oma-bot serve` delivers the answer to the waiting turn. The safety floor and hard deny rules refuse at once; `auto_allow` rules in the team file allow at once; host commands always need Spencer. head-honcho's bot changes are cards too: `oma-bot bot history BOT` and `oma-bot bot undo BOT VERSION_ID`.

Rooms: `oma-bot room list`, `oma-bot room show ROOM`, `oma-bot room post ROOM "message"`. Bots named with @Name answer (or the lead, or everyone, per the room's responder rule), at most 6 bot posts per message and 3 hops of bot mentions.

Web view (local mode): `oma-bot serve` serves it on 127.0.0.1:7477 and `oma-bot web open` prints a one-time sign-in link. Bots, approvals with answer buttons, routines, rooms, memory with undo, and the audit log.

Web-single (one owner over the internet): `oma-bot web admin-secret`, `oma-bot users add NAME --role owner < password-file`, `oma-bot users confirm NAME`, the HTTPS front from `docker/web-front`, then `oma-bot web enable --public-origin https://HOST`. Sign-in is a password plus a time-based code; see [docs/web.md](docs/web.md).

Web-multi (several users, each with a roster of their own): `oma-bot users add NAME --role owner` makes the user's roster under `users/NAME`; see [docs/multi-user.md](docs/multi-user.md), [docs/fleet.md](docs/fleet.md). It starts only after `oma-bot release check --multi` passes and the outside review is recorded.

Fleet (optional, only on Spencer's direction): `oma-bot fleet enable --by NAME --confirm`, then `oma-bot fleet invite NODE`, `oma-bot-node enroll` on the node, `oma-bot fleet approve NODE --fingerprint F`. Jobs come from `oma-bot fleet job add FILE`; see [docs/fleet.md](docs/fleet.md).

Release gate: `oma-bot release check --online` checks dashes, secrets, package pins, critical advisories, image digests and the sign-off in [docs/signoff.md](docs/signoff.md).

Exit codes: `team apply` 0 ok, 1 refused, 2 invalid file, `--check` 6 when something would change; `run` 0 ok, 3 needs input, 4 failed, 5 timed out; `doctor` 0 ok, 1 problems.

## Safety in one screen

- No bot approves its own permission requests or jobs; approvals come only from the roster broker, from Spencer or a listed approver bot that is not the requester and not under its control (tests SEC-27 to SEC-31). A pause answered any other way ends the turn, disables the bot and raises an alarm.
- A safety floor no rule or person can approve through the roster: deleting root or home folders, wiping disks, fork bombs, the roster's secrets and hard rules, the egress gate, the Docker socket. Unreadable commands are refused.
- Bots never hold the real inference key. Each bot has a per bot gateway token; the gateway holds the key.
- Bots and desktops sit on internal networks with no route out; the only way out is the egress gate, which checks every address after its own lookup and logs every decision. The supervisor starts nothing without the roster firewall rules.
- The supervisor is the only holder of docker.sock. Container specs are checked for safety in the launcher, the supervisor and the engine client: cap drop ALL, no-new-privileges, non root, ACP port on 127.0.0.1 only, host-bridge.json shadowed by /dev/null, logs on tmpfs, never docker.sock.
- Hard rules live in the data dir system prompt, outside the workspace. Writes to OPENMONO.md, .openmono, memory files and key files are denied.
- Secrets are redacted in logs and memory; `oma-bot doctor` fails when a secret shows up in a log or memory file.
- Web mode refuses to start without HTTPS through the front, the password and code login, the admin secret and an owner; sign-in locks out guessing with growing backoff, and sessions get a new id at sign-in with idle and absolute expiry.
- The web view binds 127.0.0.1 in every mode, sends a strict Content Security Policy with no inline script, checks the Host header, needs the page's Origin and a CSRF token for every change, and binds every answer to the card's action hash.
- No bot starts until a person accepts the agent image, and a changed image is refused until accepted again.
- In web-multi each user's roster is separate down to containers, networks, firewall chains, ports and tokens, and the web view opens only the signed-in account's roster.
- One host by default. Fleet mode is optional and only on Spencer's direction: nodes enroll with one-time codes and approved keys, talk mutual TLS through the relay on their own ports, and an outward step is never repeated after a crash without a person.
