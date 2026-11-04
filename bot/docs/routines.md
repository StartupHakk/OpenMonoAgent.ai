# Routines (phase 4, M4)

A routine is a scheduled job for one bot. Plain code checks run first; the model is called only when a check found something. Every run writes exactly one line to `~/.omabot/logs/routines-DATE.log` and one row in roster.db. Spencer is notified only when a run ends in `error`, `failed` or `needs_input`.

## Triggers

| Field | Example | Rule |
|---|---|---|
| `cron` | `"0 7 * * MON-FRI"` | Five fields with `L`, `W` and `#`, in the routine's IANA zone |
| `weekdays` | `"07:00"` | Stored as cron, Monday to Friday |
| `monthly` | `"last 09:00"` or `"1 09:00"` | Stored as cron; days a month does not have are skipped |
| `yearly` | `"12-25 08:00"` | Stored as cron; February 29 runs only in leap years |
| `interval_minutes` | `30` | 5 to 1440, counted from the last fire |
| `at` | `"2026-11-04 07:00"` | One time, in the routine's zone; turning it on with a past time is refused |
| `watch` | `notes`, `assignments`, `doctor`, `command:SCRIPT` | Fingerprinted every tick; fires only when it changes. The first look is a baseline. A busy bot retries in 60 seconds |

Time zones: every routine has its own zone (default: the bot's zone, which defaults to America/Phoenix). A time inside a DST gap moves forward through the gap; a repeated hour runs once, at the first occurrence. The scheduler reads the clock in UTC.

## Checks

| Kind | Arguments | Says "changed" when |
|---|---|---|
| `file_changed` | `path` (relative to the workspace) | the file's hash, or the folder's listing, differs from the last run |
| `git_new_commits` | `repo`, `branch`, optional `fetch: true` | the branch head moved. Refs are read from disk; git never runs on the host. `fetch` runs git in a throwaway container behind the egress gate |
| `http_status` | `url`, optional `field` (dotted JSON path) | the status code or field value differs. The host must be on `web.allow_hosts`; the name is resolved once, every address must be public, and the connection goes to the checked address |
| `command` | `run: "SCRIPT args"`, optional `network: true` | exit 0 with output. The script comes from `bot/checks/` and runs in a throwaway container with no network, a read-only root, the workspace read only, and a time limit |
| `doctor` | none | the set of failing `oma-bot doctor` checks is new (used by the health bot) |
| `assignments_waiting` | none | the bot has undelivered inbox rows |
| `always` | none | always |

If every check says no change, the run is `quiet` and no model call is made. Check output (at most 4 KB, redacted) goes into the routine turn as data, never as instructions.

## Runs

A run that needs the model takes the bot's turn lease (one roster turn per bot at a time), starts a fresh ACP session by default (`session: standing` uses the standing one), switches it to build mode and sends a roster-written envelope. On a bot with the `edits` or `auto` profile, changing tools (file writes, Bash and similar) are blocked for routine turns by the turn gate; the bot asks Spencer with the roster ask tool instead. HostExec is denied in routine runs unless the routine sets `host_exec: ask`, and then it always asks.

| Guard | Default |
|---|---|
| Routines per bot | 20 |
| Minimum interval | 5 minutes |
| Concurrent runs of one routine | 1 (a fire while running is `skipped_overlap`) |
| Max run time | 15 minutes, then the turn is aborted (`error`, reason `time_limit`) |
| Daily token budget | per routine (`daily_token_budget`); over budget is `skipped_budget` with one notice per day |
| Catch-up | the newest missed fire within 12 hours runs late; older ones get a `missed` row |
| Jitter | a fixed 0 to 30 seconds per routine |
| Routine share of slots | at most half the inference slots, at least 1 |

Statuses: `quiet`, `ok`, `error`, `failed`, `needs_input`, `skipped_overlap`, `skipped_budget`, `missed`. A restart never fires an occurrence twice; a run cut by a crash is marked `error` (interrupted) once and not run again.

`notify` is `errors_only` (default) or `never`. `notify: always` and `allow_external: true` are refused (hard rules 8 and 9).

## Bot tools

`routine_list` and `routine_next_runs` read. `routine_create`, `routine_update`, `routine_delete` and `routine_resume` file an approval card with the rule, the zone and the next three times; nothing changes until Spencer allows it. `routine_pause` applies at once.

## Commands

```
oma-bot routines list [BOT]
oma-bot routines runs [BOT] [ROUTINE] [--lines N]
oma-bot routines next BOT ROUTINE
oma-bot routines run-now BOT ROUTINE      # runs on the next service tick
oma-bot routines pause|resume BOT ROUTINE
oma-bot approvals [--all]
oma-bot approvals show ID
oma-bot approvals --answer ID allow|deny [--reason TEXT]
```

Log line examples:

```
2026-11-09T07:00:45-07:00 scout standup quiet checks=1 model_calls=0 ms=3
2026-11-10T07:00:45-07:00 scout standup ok checks=1 model_calls=1 tokens=340 ms=880
2026-11-11T07:00:45-07:00 scout standup error reason=inference_unreachable checks=1 model_calls=1 tokens=340 ms=910 notified=yes
```
