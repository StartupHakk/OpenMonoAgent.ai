# Approvals and the permission broker

This page describes how the roster answers a bot's pauses (plan 7.6, phase 6). Every pause in a turn the roster started goes to the approval broker. Nothing else answers a pause.

## The flow

1. A bot's turn pauses: a permission request, a playbook, a switch to build mode, a question for the user, a host command, or a finished plan.
2. The broker turns the pause into a card with a plain summary and the exact action (the command, the path, the plan text).
3. Before anyone sees it, the broker checks, in this order:
   - **Safety floor.** Never approved at any level, by anyone, through the roster: deleting the root, a system folder or a home folder; formatting or wiping a disk; fork bombs; touching the roster's secrets, database or hard rules; turning off the egress gate or the firewall; reaching the Docker socket; running downloaded or decoded text as a program. A command the checker cannot fully read (control or hidden characters, unbalanced quotes, over 8 KB) is refused, never treated as clear. Answered deny at once, logged `hard_denied`.
   - **Hard deny.** Tools that post outside the box (`mcp__x__`, `mcp__twitter__`, `mcp__facebook__`, `mcp__slack__`, mail send tools) and any action that names a host on `approvals.hard_deny_hosts` (facebook.com, fb.com, x.com, twitter.com, t.co). Answered deny at once, logged `hard_denied`, one notice per bot and tool per day.
   - **Consult and notice turns** answer only: a write, a command, a mode switch, a plan or a host command is denied.
   - **Host commands** are denied for a bot whose team entry does not set `host_exec: true`, and in routine runs unless the routine says `host_exec: ask`.
   - **Team file auto rules** (below) answer allow at once, logged `auto_allowed`. They never apply at the ask level, to work steered by outside text, to outward actions in routine runs, or to any host command.
4. Everything else is a card. It waits in `oma-bot approvals` while the pause stays open in the bot, for `approvals.pause_timeout_minutes` (10).
5. Spencer answers, or a listed approver bot answers a card bound to it. The broker posts the answer to the session's turn endpoint (permissions are answered for this one call, never for the session) and reads the rest of the turn.

## Always asks

These always show a card and are never answered by an auto rule: every host command (and `as_root: true` above all), any action in work that came from outside text (origin `outside`), outward actions in routine runs, instruction, personality and model changes, routine changes, and new bots.

Host commands, questions to Spencer, routine and instruction changes, desktop actions and fleet jobs are for a person only; an approver bot is never asked.

## Answers and time

- An answer is bound to the card's action hash (bot, session, pause id and the exact action). `--hash` refuses an answer for anything else. A card is answered once.
- After 10 minutes with no answer, the paused turn is ended (nothing of it runs) and the work waits on a follow-up question marked `pause_expired`. When Spencer answers it, the bot's next turn is told what expired and what Spencer said.
- A question to Spencer (the `ask` tool) is reminded about after `approvals.remind_after_hours` (4) and the work is cancelled after `approvals.cancel_after_hours` (24).

## No self-approval

No bot ever approves its own permission requests or its own jobs (hard rule 18). The broker fills the requester and the approver from its own records: the CLI user, a web login, or the bot socket. A field in a request body naming an approver is ignored.

- A person may answer when their role approves (owner, operator) and the card is theirs. A viewer may not.
- A bot may answer only a card the broker bound to it, of a kind Spencer listed it for (permission, playbook, toggle_mode, user_input, plan), and never when it is:
  - the requester;
  - an ancestor or a descendant of the requester's work, even after that work ended;
  - a bot the requester handed work to, or a bot the requester manages;
  - a bot the requester created or edited;
  - head-honcho or the health bot.
- A card bound to one identity and presented by another is refused. Every refusal is written to the card's events and the audit log.

### Approver bots

Spencer names approver bots in `roster.json` (`approvals.approver_bots`) or in the team file. Both lists count; the order is a preference order. `approvers: []` in the team file clears the team file's list.

```yaml
approvers:
  - { bot: Reviewer, kinds: [permission, user_input] }
```

The approver needs `mcp__roster__approval_answer` in its tools. When a card is bound to it, the roster hands it a short notice turn with the card id and the action; it answers with `approval_answer` (id, allow or deny, reason). Spencer can always answer a bound card himself.

## Team file auto rules

```yaml
bots:
  - name: Builder
    auto_allow: ["FileEdit: src/*", "Bash: dotnet test *"]
```

- `Tool`, `Tool: literal`, `Bash: prefix *` or `FileWrite: folder/*`. Paths are relative to the workspace.
- Matched against the literal text: a command with shell operators, quotes or wildcards, or a path with `..`, a `.` segment or a symlink on the way, never matches; it asks.
- Never for HostExec, playbooks, read-only tools or tools that post outside. Never for network clients, interpreters, shells, privilege or container tools.
- Tools on `approvals.always_ask` in roster.json (default HostExec, Bash, FileWrite, ApplyPatch) take no auto rule. Remove Bash from that list to allow rules like `Bash: git status`.

`bash.allow` in the team file follows the same program list: network clients and interpreters can never be allowed without asking (threat T3).

## When a bot answers itself

Inside the container a process could reach the bot's own ACP listener and answer its pause. Two layers stop it:

- The Bash pre tool hook refuses commands that name the ACP port or path, loopback addresses, the host bridge variables or the process environment.
- If an answer gets through anyway, the broker's own answer is refused by the bot (the pause is already resolved). The broker then ends the turn, disables the bot, stops its container, fails the work and raises an alarm notice.

## Host commands (agent-host)

The roster never runs host commands. For a bot with `host_exec: true` the launcher reads `pair_secret` from agent-host's own config (`~/.openmono/host-bridge.json`, which must be a 0600 regular file), asks agent-host on loopback for a time-limited invite, and passes only the invite, the agent-host address and the host workspace into the container. The pair secret never enters a container. A container holding an invite is created fresh on every start, and an idle bot sleeps before its invite runs out. Without agent-host the bot starts with host commands off and Spencer gets a notice. agent-host keeps its own deny list, sudo policy and audit trail.

## head-honcho and bot changes

head-honcho can list, create, edit, pause and resume bots only in a turn for work a person gave it directly, never in routine, outside or bot-to-bot work, and never for itself or the health bot. With an empty `manages` list it manages every worker bot.

- `bot_update` (title, description, seen_as, personality, instructions, plan_prompt, model) carries the version from `list_bots`. A stale version is refused, and a card is applied only if the bot is still at that version.
- `bot_create` makes a worker at the ask level, never with HostExec, bot tools or approval power.
- `bot_pause` applies at once. `bot_resume`, `bot_update` and `bot_create` are person-only cards.

Every applied change is a row in `bot_versions`. `oma-bot bot history BOT` lists them; `oma-bot bot undo BOT VERSION_ID` restores the spec that version replaced, and refuses when the bot changed since.

## Commands

| Command | What it does |
|---|---|
| `oma-bot approvals [--all]` | Cards waiting for an answer, with the time left |
| `oma-bot approvals show ID` | One card: action, hash, binding and every event |
| `oma-bot approvals --answer ID allow\|deny [--reason TEXT] [--hash H] [--text ANSWER]` | Answer a card; `--text` is the answer to a question |
| `oma-bot bot history BOT` / `oma-bot bot undo BOT VERSION_ID` | Bot spec versions and undo |

## Settings (roster.json)

| Key | Default | Meaning |
|---|---|---|
| approvals.approver_bots | [] | Bots and kinds Spencer allows to approve; never the requester |
| approvals.pause_timeout_minutes | 10 | How long a pause waits for an answer |
| approvals.always_ask | HostExec, Bash, FileWrite, ApplyPatch | Tools no auto rule may answer |
| approvals.remind_after_hours | 4 | Reminder for a waiting question |
| approvals.cancel_after_hours | 24 | Work waiting this long on a question is cancelled |
| approvals.hard_deny_hosts | facebook.com, fb.com, x.com, twitter.com, t.co | Hosts a command may not name |
| host_exec.admin_url | http://127.0.0.1:7476 | agent-host on loopback, where invites are minted |
| host_exec.bot_url | gateway bridge address, port 7476 | The address bots use for agent-host |
| host_exec.bridge_config | ~/.openmono/host-bridge.json | agent-host's config with pair_secret |
| host_exec.invite_ttl_seconds | 86400 | Invite lifetime (300 to 86400) |
