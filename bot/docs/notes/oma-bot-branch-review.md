# Review of the oma-bot branch (read only)

Reviewed Nov 3, 2026, about 5:50 PM MST (Phoenix). Repo: github.com/StartupHakk/OpenMonoAgent.ai, branch `oma-bot`, head 6868781. Nothing was edited, pushed or messaged. A detached worktree was used for reading at /workspace/oma-bot-wt.

## 1. What the branch contains

- Exact match found: `refs/heads/oma-bot`. No PR exists for it (checked the last 100 PRs).
- Base: it was cut from the `Windows` branch at 47d396e, not from main (merge base with main is 80d8f3b). Versus main it adds 113 files and modifies or deletes none.
  - 85 of those files are the Windows port under `windows/` (commits Oct 8 to Oct 9, two by Cursor Agent, the rest by spencer-startuphakk). Merging oma-bot into main would also merge the whole Windows port.
  - 28 files are the bot itself under `bot/` (3,164 lines, 5 commits by spencer-startuphakk, Nov 2 11:01 PM to Nov 3 4:38 PM MST).
- `bot/` layout: PLAN.md (v3, 707 lines), README.md, docs/DECISIONS.md (D1 to D14, Q1 to Q5), docs/HANDOFF.md, install.sh and install.ps1, uninstall pair, hooks (pre-tool and session-start, sh and ps1), templates (OPENMONO.md, hard-rules.md), src/Omabot (CLI `oma-bot`: version, install, doctor, team apply, status), src/Omabot/Roster (RosterDb.cs SQLite schema v1, TeamFile.cs parser, TeamApply.cs), src/Omabot.Tests (18 test methods, 24 cases).
- Ports (PLAN.md 237 to 244, Install.cs roster.json): 7477 web and API, 7478 gateway, 7500s per-bot ACP on loopback, 7600s noVNC, 3128 egress gate. Bind 127.0.0.1.
- Roster home is `~/.omabot`. Per-bot folders are `~/.omabot/bots/<slug>/{data,workspace,journal}` (TeamApply.cs 203 to 219).
- State today: phase 1 days 1 to 8 done. No container is launched yet. The supervisor, gateway, memory, scheduler, handoffs, approvals and egress gate are all still unwritten.

## 2. What may not follow our plan or Spencer's rules

Rules it does follow:
- No existing OMA file is edited. Everything is new under `bot/` and `windows/`.
- No cloud or frontier model calls. The hard rules say "never talk to a cloud model" (templates/OPENMONO.md 9 to 11).
- No telemetry, no X or Facebook code, no secrets committed. The only secret-looking strings are test fixtures (TeamFileTests.cs 84, 85, 102).
- Nothing listens on 0.0.0.0 in `bot/`.
- Posts and messages need approval (templates/hard-rules.md 7 to 10).

Problems found:
1. **The hard rules never reach a new bot.**
   - TeamApply.cs 62 calls CreateBot, which writes the rendered OPENMONO.md (line 158). Line 63 then calls EnsureBotFolders, which overwrites it with a placeholder (line 216 to 219).
   - Apply is idempotent, so the hard rules only arrive after a later spec change.
2. **The gateway address is wrong for containers.** The endpoint written into settings.json is `http://127.0.0.1:7478` (TeamApply.cs 167). Inside a bot container that is the container's own loopback, not the host. PLAN.md 241 says the gateway sits on the roster network's bridge gateway.
3. **Hooks will not work as written.**
   - OMA runs every hook with `/bin/bash -c` inside the Linux container (OMA src/OpenMono.Cli/Hooks/HookRunner.cs 99 to 101). Templates.cs 169 to 171 picks `.ps1` hooks whenever the host is Windows, so they would never run.
   - OMA passes the tool name as a `{{tool_name}}` substitution (HookRunner.cs 35 to 38, 95 to 96), not as `ROSTER_TOOL_NAME`, so pre-tool.sh line 9 logs "unknown".
   - session-start.ps1 line 8 assigns `$home`, which is PowerShell's read-only `$HOME`.
   - pre-tool.ps1 lines 17 to 18 call Join-Path on a possibly null value before the fallback.
4. **The memory load path (C4) cannot work on main.** PLAN.md 169 to 178 makes session_start hook output the main way memory loads. OMA throws that output away (HookRunner.cs 24 to 30). That answers Q1 (DECISIONS.md 37) from code, so the plan should fall back to the OPENMONO.md block now.
5. **Two settings.json keys are mis-shaped.**
   - `api_key` is set to the literal `$env:OPENMONO_API_KEY` (Templates.cs 181). OMA does not expand it, and only overrides it when the env var is set (ConfigLoader.cs 234 to 236).
   - `web_search` (Templates.cs 255) is not an OMA key. OMA reads `web.search` (AppConfig.cs WebConfig), so WebSearch is not actually turned off, and it falls back to DuckDuckGo (WebSearchTool.cs 30 to 31, 52).
6. **A bot can edit its own hard rules.** OPENMONO.md lives in the workspace (TeamApply.cs 154 to 158), and the `edits` profile allows writes to `/workspace/**` (Templates.cs 208).
7. **Secret handling leaks.**
   - The team.yaml refusal message prints the first 28 characters of the found secret (TeamFile.cs 480).
   - D12 (DECISIONS.md 30) says no secret rotation is needed. Our plan asks for rotating the secrets seen in the Server Building Bot chat and the echo20 process list.
8. **Per-bot isolation is not complete.**
   - Workspace paths are not normalized, so `/srv/work/../etc` would pass (TeamFile.cs 448 to 451).
   - Two bots may share one project workspace, and nothing checks for that.
   - Per-bot container name, port and data dir exist only in docs so far (DECISIONS.md 115 to 125, HANDOFF day 9).
9. **Doctor bugs.**
   - `Working.Root` is empty when the binary is outside the repo, and `StartsWith("")` is always true, so that check always fails (Doctor.cs 106).
   - A Docker failure is silently removed from the failure list (Doctor.cs 72 to 77), which goes against the fail-closed rule.
10. **Installer gaps.**
    - `bot/dist/` (install.sh 399 to 405, install.ps1 91) is not in .gitignore.
    - install.ps1 118 passes `-Disabled` to Register-ScheduledTask. I believe that is not a valid parameter (verify on the box).
    - `--purge-roster-home` deletes bot workspaces too (uninstall.sh 36).
11. **Audit rows are written twice.** Create is audited at TeamApply.cs 66 and 125, and update at 73 and 183.
12. **Dashes.**
    - An em dash at bot/src/Omabot/Roster/Status.cs 8.
    - Em dashes at windows/docs/BUILD-PLAN.md 253 and 794.
    - The PLAN.md title uses a spaced hyphen (line 1).
13. **The UI decision contradicts itself.** DECISIONS.md D3 (line 18) says framework-free ES modules. PLAN.md 556 says Razor plus htmx.
14. **The schema is missing fields the plan requires.**
    - `assignments` has no mode, depth, root, parent, budget or dedupe columns (RosterDb.cs 73 to 78), which PLAN.md 109 requires.
    - `routines` has no pre-check or notify-on-error fields (RosterDb.cs 87 to 94).
15. **A broken MCP entry.** settings.json lists `roster/mcp-shim/run` (Templates.cs 187), which does not exist yet.
16. **License.** No NOTICE yet. That is acceptable while no OpenMausBot code is copied (README 19). Nothing from enterprise/ was found.

## 3. What it does better, worth adopting

- A root `bot/` folder instead of our `src/OpenMono.Roster`, so OMA's solution and build stay untouched. It also has its own installer, which requires OMA first.
- A separate `~/.omabot` home.
- team apply never deletes, renames through `was:`, uses spec hashes and is idempotent. It refuses secrets in team.yaml.
- The image digest is pinned with `images accept`.
- The gateway uses slot affinity for cache hits (C3). The gateway holds the only inference key, and bots get per-bot tokens.
- Routines can use OMA playbooks as their body. Evidence uses ACP diff ids. HTTP 409 session_busy counts as the admission signal. Duplicate requests join one turn.
- Windows is a first-class platform.
- The egress gate is its own small .NET code.
- SQLitePCLRaw is pinned against GHSA-2m69-gcr7-jv3q.
- The web view comes early (phase 3).
- A drift test (INT-08) checks the launch spec against compose.

## 4. Gaps versus our phases 0 to 9

- Phase 0: Q1 to Q5 are open and the capacity table is not filled.
- Phase 1: half done. Missing are the container spec renderer, supervisor, gateway v0, up, down and run, doctor v1, and the two-bot demo (SCN-01).
- Phases 2 to 9 do not exist yet: gateway queue and redactor, memory with redaction and journal, scheduler with code checks and errors-only notify, handoffs, approvals broker, egress gate and desktops, tool-call repair layer, rooms, load tests and NOTICE.
