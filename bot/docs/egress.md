# Egress: networks, the gate and the firewall

This page describes how roster containers reach the network (plan 7.7, phase 7). The short version: bots and desktops have no route anywhere except through the roster. Every way out is checked by address, logged, and backed by firewall rules the supervisor verifies before it starts anything.

## Networks

| Network | Subnet | Bridge | Internal | Who joins |
|---|---|---|---|---|
| oma-roster | 172.30.240.0/24 | br-omabot0 | yes | bot agents, the gate (as `egress`) |
| oma-desk | 172.30.241.0/24 | br-omabot1 | yes | desktops, bots with a desktop, the gate (as `egress`) |
| oma-egress | 172.30.242.0/24 | br-omabot2 | no | the gate only |

- Internal networks have no default route. A tool that ignores the proxy gets no connection at all.
- The gateway listens on 172.30.240.1 (`gateway.bind_address` must be the bots network gateway). Bots call it directly; `NO_PROXY` is set to that address.
- The bridges have fixed names so the firewall rules can key on them. They start with `br-` so agent-host binds the bots bridge when a bot has `host_exec`.
- The supervisor creates the networks with exactly these settings. If a network with the same name exists with other settings, it refuses to start anything and says so; remove the network (`docker network rm NAME`) and the roster creates it again.

## The firewall rules (fail closed)

Under the gate there is a second layer: iptables rules in two chains of the roster's own.

- `OMABOT-USER` (jumped to from `DOCKER-USER`): nothing is forwarded in or out of the bot and desktop bridges; the egress bridge cannot reach 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, multicast or reserved ranges.
- `OMABOT-INPUT` (jumped to from `INPUT`): from the bots bridge, only the gateway port on 172.30.240.1 (and agent-host on 7476 when `host_exec.open_firewall` is true); from the desktop and egress bridges, nothing on the host.

Print the script and apply it once with sudo (and again after changing networks in roster.json):

```bash
oma-bot egress rules > ~/.omabot/firewall.sh   # inside the 0700 roster home, not /tmp
less ~/.omabot/firewall.sh
sudo sh ~/.omabot/firewall.sh
oma-bot egress check
```

The supervisor reads the filter table before every start of a bot, the gate, a desktop or a networked check, and refuses when a rule is missing or the table cannot be read. It runs `iptables-save -t filter`, directly or through a sudo rule that allows exactly that command:

```
# /etc/sudoers.d/oma-bot (replace spencer with the account that runs the supervisor)
spencer ALL=(root) NOPASSWD: /usr/sbin/iptables-save -t filter
```

`egress.firewall_check: off` in roster.json turns the check off. It exists only as Spencer's explicit override; leave it at `required`.

## The egress gate

The gate is a small HTTP proxy in its own container (`oma-roster-egress`, image built from `docker/egress/Dockerfile`).

- Clients name themselves with Basic proxy credentials carrying a token: each bot agent (`BOT`), each bot's command checks (`check.BOT`) and each desktop (`desk.NAME`). Tokens live 0600 in `secrets/egress/`; the gate's policy file holds only their hashes.
- `CONNECT host:443` makes a tunnel. An absolute `http://` URL is forwarded once with `Connection: close`. Only ports 80 and 443.
- Before connecting, the gate checks the port, the host name, the blocked hosts and the client's allowlist; then it resolves the name itself, refuses unless every address is public, and connects to exactly the address it checked. A redirect is a new request and is checked again.
- IP addresses are never on an allowlist. Facebook (facebook.com, fb.com, messenger.com, fbcdn.net) is always blocked. X and Twitter are in `egress.blocked_hosts`; Spencer can remove them by hand.
- At most 32 open connections per client; a tunnel lasts at most an hour.
- Every decision is one JSON line in `logs/egress/egress.log` (client, host, port, allowed or refused, reason, address). No token and no URL path is logged.
- 20 refusals for one client in 10 minutes raise one alarm notice (`egress.alarm_refusals`, `egress.alarm_minutes`).

Allowlists come from each bot's `web.allow_hosts` in the team file. A desktop uses its owner's list (own) or the list of the bot holding its lease (shared, pool), and none while it is free.

## Search and scrape for bots

OMA's WebSearch and WebFetch ask the gateway first. The roster gateway serves:

- `GET /v1/services`: whether this bot may search (`web.search` and the WebSearch tool) and scrape (`web.scrape` and the WebFetch tool).
- `GET /v1/search`: forwarded to SearXNG on the inference server (`gateway.search_upstream`, through the relay).
- `POST /v1/scrape`: forwarded to Scrapling (`gateway.scrape_upstream`) only for a URL whose host is on the bot's `web.allow_hosts`, never blocked and never an IP address.

The relay address and key stay with the gateway. OMA's direct-fetch fallback cannot use the gate (its own SSRF guard refuses a proxy on a private address) and has no route, so it fails closed.

## Commands

| Command | What it does |
|---|---|
| `oma-bot egress status` | Networks, policy, allowed and refused counts for the last hour, each bot's hosts |
| `oma-bot egress rules` | The firewall script to run with sudo |
| `oma-bot egress check` | Whether the rules are in place (exit 1 when not) |
| `oma-bot egress log [--lines N] [--refused]` | Recent gate decisions |
