# Install and first run

The roster runs on one Linux host by default (the agent box). Inference stays on the inference server and is reached only through the gateway and the FRPS and FRPC relay.

## What you need

- Linux with Docker, and the account that runs the roster in the `docker` group only for the supervisor (see below).
- The OMA agent image (`openmono-agent:latest`) built from this repository.
- The relay endpoint for the inference server and its key.
- To build from source: the .NET 10 SDK (`global.json` pins the version).

## Build and install

```bash
cd bot
dotnet build Omabot.sln
dotnet test Omabot.sln          # skipped tests name what they need (Docker, the relay, a GPU)
./install.sh                    # per user, no sudo; --systemd also writes the two user units
```

## First run

```bash
oma-bot init                                     # ~/.omabot, roster.json, team/team.yaml
$EDITOR ~/.omabot/roster.json                    # gateway.upstream = the relay address (https)
oma-bot secrets set inference < /path/to/keyfile # stored 0600, never on the command line; only the gateway reads it
oma-bot team apply ~/.omabot/team/team.yaml
oma-bot egress rules > ~/.omabot/firewall.sh    # read it, then apply it once
sudo sh ~/.omabot/firewall.sh
oma-bot egress check
oma-bot-supervisor &                             # the only process with Docker access
oma-bot images accept                            # after checking the agent image; nothing starts before this
oma-bot doctor
oma-bot serve &                                  # networks, gateway, scheduler, broker, web view
oma-bot up
oma-bot web open                                 # sign in to the web view on this computer
oma-bot run head-honcho "Hello, who are you?"
```

The supervisor refuses to start any container until the firewall rules are in place (docs/egress.md). To let it read the rules without root, add the sudo rule shown there.

## Web-single

To reach the view over the internet with one owner account, follow docs/web.md: the admin secret, an owner with a second factor, the HTTPS front from `docker/web-front`, then `oma-bot web enable`.

## Web-multi

Several users, each with a roster of their own, after the release gate and an outside review: docs/multi-user.md.

## Images

| Image | Build |
|---|---|
| openmono-agent | from the OMA repository |
| oma-roster-egress | `docker build -f docker/egress/Dockerfile -t oma-roster-egress:latest .` |
| oma-roster-desktop (only with desktops) | docker/desktop/Dockerfile with a pinned base digest (docs/desktops.md) |
| caddy (only for web-single) | pulled by digest by docker/web-front/compose.yaml |

## Capacity

Measure before you trust any number: `oma-bot gateway slots`, then `oma-bot gateway bench --save` and `oma-bot gateway toolbench`. See docs/capacity.md.

## Upgrades

Stop the service, install the new build, run `oma-bot doctor`, start the service. roster.db and users.db migrate on start; migrations only add.

## Uninstall

`./uninstall.sh` removes the installed programs and user units. It leaves `~/.omabot` (bots, memory, secrets, audit) for you to keep or delete.
