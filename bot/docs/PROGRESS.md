# Build progress

Times are America/Phoenix. Test counts are from `dotnet test Omabot.sln` on the build box (Linux, .NET 10.0.403, no Docker).

| Phase | Milestone | Status | Commit | Tests (passed / skipped) |
|---|---|---|---|---|
| 0 | M0 | Recorded; open items need Spencer, the relay and hardware (docs/phase0/VERIFY.md) | 44762ea | none |
| 1 | M1 | Code and tests done; SCN-01 and live Docker checks skipped (no Docker on the build box) | 2314c41 | 101 / 9 |
| 2 | M2 | Code and tests done; LOAD-01 passes against a simulated upstream, live LOAD-01, INT-07 live, INT-13 and LOAD-06 skipped (need the relay and the main box) | 1ba39fc | 141 / 13 |
| 3 | M3 | Code and tests done; SCN-02 and INT-04 skipped (need Docker and a live gateway) | see git log | 171 / 15 |

## Phase 1: bot launcher, supervisor and CLI skeleton

Done:
- Team file parser and resolver (plan 7.13) with refusals by field and line, plan output, apply with one audit row per change, never deletes, export.
- roster.db schema v1 (bots, sessions, failures, audit hash chain with append only triggers, notices, team items).
- Rendering of settings.json, system-prompt.md (hard rules, outside the workspace), OPENMONO.md (capped at 200 lines and 25 KB), permission profiles ask, edits and auto, bash hooks.
- Container spec (launch option B) with labels and the OMA shadowing rules, checked in three layers; Docker Engine client over the unix socket; supervisor host on a 0600 socket with bearer auth.
- Launcher: discovery wait, standing session stored, health probe, backoff 5 s, 30 s, 2 min, 10 min, then disabled with one notice (B19).
- Pass-through gateway: per bot token check, model nickname swap, real key held only by the gateway, plain http upstream refused off loopback (B1), `inference unreachable` state.
- CLI: init, team apply and export, up, down, status, run, events, doctor, enable, disable, notices, audit, serve, render, version.
- Default team: head-honcho (lead) and health (read only, own roster entry).
- install.sh and uninstall.sh (per user, no sudo).

Skipped tests and why:
- SCN-01, INT-02, INT-05, SEC-01 live, SEC-08 live, SEC-09 live, SEC-16 live, SEC-21 live: need Docker, the openmono-agent image, the relay or a second LAN host.
- REG-01: OMA's own suite runs in OMA CI; REG-02 proves no OMA file changed.

## Phase 2: roster gateway, capacity and secrets

Done:
- LaneQueue per backend: S lanes, four priorities, round robin by bot, per bot cap of 2, background drop rule (B23), sticky lanes, cancel, live S changes.
- Slot discovery from Strata `/v1/status` and llama-server `/slots`, measured override order, refresh loop and unknown slots notice (B21).
- Label routing over backends with per backend model names; implicit main backend from `gateway.upstream` (B22).
- Usage metering per bot and routine (roster.db `usage`), `oma-bot usage`; streamed bytes pass through unchanged (B24).
- Turn context table so the roster, not the bot, sets priority; `oma-bot run` marks turns interactive.
- `oma-bot gateway status | slots | bench [--save]`; bench report in `~/.omabot/reports/`.
- `oma-bot secrets set | list | rm | rotate-bot`, per bot folders, team file secret materializing, startup secrets guard, redactor loaded with every stored value. See docs/secrets.md and docs/gateway.md.

Tests added: UNIT-GW-01, 02, 03, 05, INT-07 (stub server), LOAD-01 (simulated), SEC-05 (unit), SEC-08 (spec), SEC-12 exit check, secrets CLI, guard, bench.

Skipped and why:
- LOAD-01 live, INT-07 live, INT-13, LOAD-06: need the real inference server through the FRPS and FRPC relay and the main box.

## Phase 3: per-bot memory (M3)

- Core: Memory/MemoryEntries, MemoryBudget, MemoryStore (revision checks, redaction, budget, atomic writes, journal, undo, symlink refusal, direct write scan), Recall, MemoryUpkeep (capture, organize, tidy), MemoryTools.
- Roster tools host and per-bot shim sockets (Api/RosterTools, Api/ShimServer) with per-turn tokens; the stdio MCP shim in bot/mcp-shim.
- `oma-bot run` refreshes OPENMONO.md with memory, adds recall notes, logs the turn and records it for capture. `oma-bot serve` runs the shim sockets and upkeep at background priority through the gateway.
- Tests: UNIT-MEM-01 to 09 plus revision, budget, limits, symlink, direct write, forget, recall, capture, tidy cap and shim round trip.
- Skipped: SCN-02 (Docker, live gateway), INT-04 (Docker).
- Decisions B25 to B30 need Spencer review.
