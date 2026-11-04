# CLI reference

`oma-bot COMMAND [args]`. `oma-bot help` prints the same list. Every command works headless; nothing needs the web view.

Exit codes: 0 ok, 1 refused or failed, 2 invalid input, 3 needs input (a card is waiting), 4 turn failed, 5 timed out, 6 `team apply --check` found changes.

### version
Prints the version.

### init
Creates `~/.omabot` with `roster.json` and a starter `team/team.yaml` (head-honcho and the read-only health bot). Never overwrites.

### team
`team apply FILE [--dry-run | --check]` checks the file, prints the plan and applies it; `team export [--out FILE]` writes what is stored. See docs/team-file.md.

### up
`up [BOT]` starts every enabled bot, or one, through the supervisor.

### down
`down [BOT]` stops every bot, or one.

### status
`status [BOT]`: state, port and last error.

### run
`run BOT "message" [--json] [--mode plan|build] [--wait SECONDS] [--wait-done SECONDS] [--timeout MINUTES]`: one turn as a root assignment. Exit 3 means a card is waiting; the output names it.

### assignments
`assignments [list] [--all] | show ID | cancel ID [--reason TEXT]`: handed-out work (docs/handoffs.md).

### events
`events BOT [--lines N]`: the bot's recent turn events.

### doctor
`doctor [--json] [--quiet] [--online]`: health checks; exit 1 when something fails.

### enable
`enable BOT`: re-enables a disabled bot (after the self-answer alarm, check its events first).

### disable
`disable BOT`: stops and disables a bot.

### notices
`notices [--seen ID]`: unseen roster notices.

### audit
`audit tail [N] | audit verify | audit tree ROOT_ID`: the append-only audit log, its hash chain, and one assignment tree.

### serve
`serve [--once] [--dev]`: the roster service: networks, gateway, health monitor, scheduler, handoff runtime, approval broker, rooms, egress policy and gate, desktops, and (local mode) the web view.

### fleet
`fleet enable --by NAME --confirm | disable | status | invite NODE [--labels k=v,..] [--never-touch] | approve NODE --fingerprint F | pause | resume | drain NODE | revoke NODE | job add FILE | job show ID | job cancel ID | jobs | secret set NAME`: optional fleet mode (docs/fleet.md). Run by a person on the roster host only; every command but `enable` refuses while fleet mode is off. Job secrets come on standard input.

### gateway
`gateway status | slots [--backend NAME] | bench [--backend NAME] [--levels 1,2,4] [--save] | toolbench [--backend NAME] [--levels 1,2,4]`: gateway state, slot discovery, the capacity bench and the 40-task tool bench (docs/gateway.md, docs/local-models.md).

### secrets
`secrets set NAME [--bot BOT] [--file PATH]` (value on stdin) `| list [--bot BOT] | rm NAME [--bot BOT] | rotate-bot BOT` (docs/secrets.md).

### usage
`usage [--bot BOT] [--days N] [--json]`: model calls and tokens per bot and routine.

### memory
`memory show|journal [--lines N]|undo ID|forget "fact" [--topic T]|scan|tidy BOT` (docs/memory.md).

### render
`render BOT`: writes the bot's files without starting it and prints their paths.

### routines
`routines list [BOT] | runs [BOT] [ROUTINE] [--lines N] | next|run-now|pause|resume BOT ROUTINE` (docs/routines.md).

### approvals
`approvals [--all] | show ID | --answer ID allow|deny [--reason TEXT] [--hash H] [--text ANSWER]` (docs/approvals.md).

### bot
`bot history BOT | bot undo BOT VERSION_ID`: bot spec versions and undo.

### images
`images [status] | accept [--digest sha256:...]`: the agent image bots run. No bot starts until a person accepts the image on this host, and a changed image is refused until accepted again (SEC-14). `--digest` makes the acceptance fail unless the image on the host is exactly that one.

### release
`release check [--repo DIR] [--advisories FILE | --online] [--multi]`: the automatic part of the security release gate (plan 11.8, docs/release-checklist.md): no dash characters, no secrets in docs, templates or the README, every package pinned, no known critical advisory against a pinned package (from a JSON file or `dotnet list package --vulnerable`), image recipes that require digests, and Spencer's sign-off in docs/signoff.md (`--multi` also needs the outside review). Exit 1 means the release is blocked.

### egress
`egress status | rules | check | log [--lines N] [--refused]`: the egress gate and the firewall rules (docs/egress.md).

### desktops
`desktops [list] | show NAME | stop NAME` (docs/desktops.md).

### room
`room list | show ROOM [--lines N] | post ROOM "message"` (docs/rooms.md).

### web
`web open | enable [--public-origin URL] | disable | status | admin-secret [--rotate]`: the web view and web-single mode (docs/web.md). `open` prints a one-time sign-in link (local mode only). `enable` refuses without HTTPS through the front, a login method that is built, the admin secret and an active owner, and checks that the public address has a valid certificate and reaches this roster. `disable` returns to local mode and ends every web session. `admin-secret` creates the secret (never printed); `--rotate` replaces it and re-seals every account's second factor.

### users
`users add NAME --role owner|operator|viewer [--home ROSTER] | confirm NAME | list | homes | role NAME ROLE | disable NAME | enable NAME | logout NAME | password NAME`: web accounts. In web-multi an owner gets a roster of their own under `users/NAME` (made on first use with isolated settings), an operator or viewer names one with `--home`, and `homes` lists the user rosters with their slots and port blocks (docs/multi-user.md). Passwords and codes come on standard input, never as arguments. `add` writes the authenticator key to `web/enroll/NAME.txt` (0600); `confirm` checks a current code, activates the account and removes the file. The last active owner cannot be disabled or demoted; changing a password or role, disabling, and `logout` end the account's sessions.
