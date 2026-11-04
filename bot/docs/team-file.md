# Team file reference

The team file (YAML) defines bots, teams, rooms, routines and routing (plan 7.13). `oma-bot team apply FILE` checks it against live state, prints a plan, and applies it step by step. Apply never deletes; a key the file leaves out keeps its stored value. Unknown keys are errors, reported with the field and the line.

```bash
oma-bot team apply team.yaml --dry-run   # plan only
oma-bot team apply team.yaml --check     # exit 6 when something would change
oma-bot team export                      # what is stored, as a team file
```

## Top level

| Key | Type | Notes |
|---|---|---|
| version | 1 | Required |
| defaults | map | model, permission_profile, timezone, idle_sleep_minutes, memory, limits, host_exec, desktop for bots that leave them out |
| models | map | Nicknames bots use: `upstream_model` (required), `context_size`, `backend`, `labels` |
| teams | list | `name`, `lead` (a bot), `brief` |
| bots | list | See below |
| routines | list | Routines given at the top level carry `bot:` |
| routes | list | Keyword routing for `assign(to: auto)`: `{ words: [...], to: Bot }` |
| rooms | list | See Rooms |
| blocked_hosts | list | Host names no bot may allow (x.com blocks api.x.com) |
| approvers | list | `{ bot: Name, kinds: [permission, playbook, toggle_mode, user_input, plan] }`; `approvers: []` clears the list (docs/approvals.md) |

## Bots

| Key | Default | Notes |
|---|---|---|
| name | required | Up to 60 characters, unique ignoring case; its slug names the folders |
| was | | Earlier name, to rename a bot |
| team | | Team name |
| role | worker | worker, lead (head-honcho, one only) or health (read only) |
| title | | Up to 200 characters |
| description, seen_as | | How other bots see it (routing) |
| personality | required | Text or `{ file: path }`, up to 24,000 bytes; goes into the data dir system prompt |
| plan_prompt | | Extra text for plan mode |
| instructions | | Up to 200 lines and 25 KB (OMA's OPENMONO.md cap, shared with memory) |
| model | from defaults | A nickname from models or the gateway backends |
| permission_profile | ask | ask (every write, command and host command asks), edits (file edits in the workspace are allowed), auto (bash.allow commands are allowed) |
| tools | | `allow` and `deny` lists of OMA tools and `mcp__roster__*` tools; everything else is denied. Keep a worker at 12 or fewer |
| bash | | `allow` and `deny` command patterns; network clients, interpreters, shells, sudo and docker are refused in allow |
| workspace | auto | auto (bots/NAME/workspace) or an absolute path under `workspace_roots` |
| shared_workspace | false | true to let two bots share a workspace |
| host_exec | false | Host commands through agent-host; every one needs Spencer (docs/approvals.md) |
| desktop | off | off, own, shared or pool (docs/desktops.md) |
| web | | `search` and `scrape` (through the gateway), `allow_hosts` for the egress gate and scrape |
| memory | on | `enabled`, `max_lines` (10 to 2000), `max_bytes`, `upkeep: { capture, tidy }`, `recall` |
| timezone | America/Phoenix | An IANA zone |
| idle_sleep_minutes | 60 | 0 keeps the bot running |
| limits | | `memory` (like 1536m), `cpu`, `pids`, `max_turn_minutes`, `daily_token_budget` |
| routines | | See Routines |
| rooms | | Room names |
| accepts_from | [person] | Bots that may hand it work (Spencer always may) |
| manages | | Bots it manages (head-honcho with an empty list manages every worker) |
| keywords | | Routing words |
| secrets | | `NAME: { file: path }` or `NAME: { env: VAR }`; never a value (docs/secrets.md) |
| auto_allow | | Team file auto rules for the approval broker (docs/approvals.md) |
| preset | | A sampling preset from `bots.presets` in roster.json (docs/local-models.md) |

## Routines

| Key | Notes |
|---|---|
| name | Unique per bot |
| one trigger | `cron` (with its own `timezone`), `interval_minutes` (5 to 1440), `at` (one time), `watch` (a path, the inbox, doctor results or a check script), or a preset: `weekdays: "08:00"`, `monthly: "1 09:00"` or `"last 09:00"`, `yearly: "12-25 08:00"` |
| checks | file_changed, git_new_commits, http_status, command, assignments_waiting, doctor, always; quiet runs make no model call |
| instructions | What the bot does when the checks say so |
| notify | errors_only (the only value; bots never post outside, hard rule 8) |
| session | fresh (default) or standing |
| host_exec | off (default) or ask |
| daily_token_budget | 50000 |
| enabled | true |

`allow_external: true` is always refused (hard rule 9). See docs/routines.md.

## Rooms

| Key | Default | Notes |
|---|---|---|
| name | required | |
| team | | Its lead answers with the lead rule |
| members | required | Bots |
| responder | mentions | mentions (bots named with @ answer), lead, or everyone (each member once, in order) |
| brief | | Up to 12,000 characters |
| max_posts_per_hour | 30 | Bot posts per hour in the room |

See docs/rooms.md for the room limits.

## Example

This adds to the starter team that `oma-bot init` writes (head-honcho and the health bot), which is applied first.

```yaml
version: 1
defaults:
  model: flash-next
  timezone: America/Phoenix
models:
  flash-next:
    upstream_model: Qwen3.8-Flash-Next
    context_size: 65536
teams:
  - { name: Core, lead: head-honcho }
bots:
  - name: Builder
    team: Core
    title: Builds and tests
    personality: { file: bots/builder.md }
    permission_profile: edits
    preset: tools
    tools: { allow: [FileRead, FileEdit, FileWrite, Grep, Glob, Bash, mcp__roster__assign, mcp__roster__ask] }
    bash: { allow: ["dotnet build *", "dotnet test *", "git status"] }
    accepts_from: [person, head-honcho]
    routines:
      - name: nightly-tests
        weekdays: "02:00"
        checks: [{ git_new_commits: { repo: app, branch: main } }]
        instructions: Run the tests and report failures only.
rooms:
  - { name: planning, team: Core, members: [head-honcho, Builder], responder: lead }
```
