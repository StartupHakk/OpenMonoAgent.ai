# Web-multi: several users on one host

Web-multi runs one web view for several users on a host someone else operates (plan 7.19, 7.20). Each user gets a whole roster of their own; users never see or reach each other's bots, cards, memory, rooms or audit. It starts only after the security release gate and an outside review (plan 11.8).

## What each user gets

`oma-bot users add NAME --role owner` (with `mode: web-multi` in roster.json) makes the user's roster under `~/.omabot/users/NAME` with its own:

| Part | Per user |
|---|---|
| roster.db, audit, logs, memory, bots, data folders | inside `users/NAME` |
| Secrets and gateway tokens | `users/NAME/secrets` |
| Container names | prefixed with the slot (`u01-oma-bot-scout`) and labelled; the user's supervisor lists and changes only its own |
| Docker networks and bridges | `oma-u01-roster`, `oma-u01-desk`, `oma-u01-egress` on `br-omau01b`, `br-omau01d`, `br-omau01e` |
| Subnets | `172.31.(4 x slot).0/24` and the next two |
| Firewall chains | `OMABOT-U01-USER`, `OMABOT-U01-IN` |
| Ports | a block of 200 from `20000 + 200 x slot`: API, gateway, desktops, bots |
| Workspace root | `users/NAME/work` |

Host commands (`host_exec`) are refused in user rosters: the host belongs to its operator. The inference upstream and key are the operator's; each user's gateway reads the operator's key file, and bots only ever hold their own gateway tokens.

## Running it

```bash
oma-bot users add alice --role owner < password-file      # makes users/alice
oma-bot users confirm alice <<< 123456
oma-bot users add carol --role viewer --home alice < password-file
oma-bot users homes                                       # slots, homes and port blocks
OMABOT_HOME=~/.omabot/users/alice oma-bot team apply alice-team.yaml
OMABOT_HOME=~/.omabot/users/alice oma-bot egress rules > ~/.omabot/users/alice/firewall.sh
sudo sh ~/.omabot/users/alice/firewall.sh
OMABOT_HOME=~/.omabot/users/alice oma-bot images accept
systemctl --user enable --now oma-bot-supervisor@alice oma-bot@alice
```

`./install.sh --systemd` writes the `oma-bot-supervisor@.service` and `oma-bot@.service` templates. The operator's own `oma-bot serve` (with `mode: web-multi`) serves the web view; each user's service runs that user's bots, scheduler, broker and rooms.

## The web view

The same view as web-single, behind the same HTTPS front and sign-in. Every API call opens only the signed-in account's own roster; no URL, query or header can name another one. An owner reads their roster's audit; operators answer cards; viewers read. An account without a roster gets nothing.

## Before it starts

`oma-bot serve` refuses web-multi until `~/.omabot/web/release-signoff.md` (0600) says:

```
security_gate: passed
outside_review: done
signed_by: NAME
date: YYYY-MM-DD
```

Fill it in only after `oma-bot release check --multi` passes, the tests listed in plan 11.8 are green (SEC-01 to SEC-33 and HEALTH-01 to HEALTH-04 for two users), and the outside review is done.
