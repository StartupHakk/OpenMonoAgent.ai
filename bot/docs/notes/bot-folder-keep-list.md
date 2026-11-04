# bot/ folder keep list (old oma-bot branch)

Written Nov 3, 2026 (America/Phoenix) before the oma-bot branch was rebuilt from main. Source: origin/oma-bot at 6868781, folder bot/, 28 files, 3,164 lines. The old branch also carried 85 Windows port files under windows/, which are not part of this list and are dropped with the rebuild. A local backup branch named backup/oma-bot-before-rebase-2026-11-03 points at 6868781.

## Keep or adapt

| Path on the old branch | What is worth keeping | How it is used now |
|---|---|---|
| bot/templates/hard-rules.md | Ten short, well written hard rules for every bot (no cloud models, no posting, approvals for outward actions, no secrets in memory) | Adapt into the hard rules block that lives in each bot's data dir system prompt, outside the workspace (plan lesson L2) |
| bot/docs/DECISIONS.md D1 | Main box is 64 GB RAM, 24 GB VRAM, Qwen3.8-Flash-Next, start at 2 slots | Matches Spencer's Nov 3 hardware answer. The 2 slots figure is kept only as an example to measure (plan 7.9, task C1) |
| bot/docs/DECISIONS.md D12 | Secrets are contained on the box with no cleanup demanded of Spencer | Matches Spencer's Nov 3 secrets answer (plan 7.12) |
| bot/docs/DECISIONS.md D13, D14 | The `~/.omabot` roster home and the separate bot/ folder with its own solution | Kept (plan 6.4, 6.5) |
| bot/docs/HANDOFF.md | Build traps: pin SQLitePCLRaw.bundle_e_sqlite3 2.1.12 (advisory GHSA-2m69-gcr7-jv3q), the MSB4066 PackageReference trap, the C# nullable tuple `default` trap | Copy the facts into the build notes when phase 1 starts |
| bot/docs/HANDOFF.md phase 0 checklist | Hand launched `docker run` recipe that shadows host-bridge.json with /dev/null, and a curl of `/v1/status` | Reuse as phase 0 tasks 6 and 10 |
| bot/PLAN.md (v3) | Slot affinity (C3), an early web view (C5), playbooks as routine bodies, ACP diff ids as evidence, HTTP 409 session_busy as the admission signal, joining duplicate requests (C7), the egress gate as a small .NET program, the secrets inventory table, security test ideas SEC-05, SEC-12, SEC-16 | Already folded into the plan (sections 4.9, 7.4, 7.5, 7.7, 7.9, 7.12, 11.4) |
| bot/src/Omabot.Tests/TeamFileTests.cs | Test names worth keeping: ValidMinimalParses, UnknownFieldRefused, DuplicateNamesRefused, RenameOntoExistingNameRefused, CredentialValuesInFieldsRefused, CredentialFieldInsidePersonalityTextRefused, OversizedPersonalityRefused, WorkspaceOutsideAllowedRootsRefused, ApplyTwiceIsIdempotent, SpecHashChangesWhenWorkspaceChanges, SlugsCollapseSpecials, SchemaV1CreatesAllTables, ConfigUpsertsAndAuditAppends, SettingsJsonParses, HooksTemplatedByPlatform, OpenMonoMdHasNoStructuralPlaceholdersLeft, TemplatesCarryNoTypographicDashes | Map onto UNIT-TEAM-01 to 10 and REG-06 when tests are written. HooksTemplatedByPlatform changes meaning: hooks are bash only |
| bot/src/Omabot/TeamFile.cs, TeamApply.cs | Ideas: `was:` renames, spec hashes for idempotent apply, apply never deletes, secrets in team.yaml refused | Re-implemented on main without the bugs in plan lessons L1, L10, L12, L16 |
| bot/src/Omabot/RosterDb.cs | Schema v1 table list as a starting checklist | Rebuilt with the columns from plan 7.4 and 7.5 (lesson L19) |
| bot/.gitignore | The idea of a bot-only ignore file | Rebuilt with bot/dist/ added (lesson L15) |

## Drop

| Path on the old branch | Why it is dropped |
|---|---|
| bot/README.md | Mentions `doctor` renaming and a NOTICE file, both superseded. Replaced by a short plan-only README |
| bot/hooks/*.ps1 and *.sh | PowerShell hooks cannot run in the Linux container. pre-tool reads ROSTER_TOOL_NAME, which OMA never sets, and session-start output is discarded by OMA (lessons L4 to L7) |
| bot/install.sh, install.ps1, uninstall.sh, uninstall.ps1 | Windows first, purge deletes workspaces, `-Disabled` flag believed invalid (lesson L15). Installers are rewritten in phase 1 |
| bot/src/Omabot/Doctor.cs | Root check always fails outside the repo and Docker failures are hidden (lessons L13, L14). The health check is rewritten |
| bot/src/Omabot/Templates.cs | Wrong gateway address 127.0.0.1, literal `$env:` api key, wrong `web_search` key, missing MCP path (lessons L3, L8, L9, L20) |
| bot/src/Omabot/Status.cs | Contains an em dash (lesson L17); trivial to rewrite |
| bot/src/Omabot/Cli.cs, Install.cs | Built for the Windows layout; rewritten on main |
| bot/docs/DECISIONS.md D2, D5 | D2 called agent-mosaic not upstream; Spencer now says it lives on Joshua's branch and is out of scope. D5 chose LAN or Tailscale with no relay, which is reversed: the FRPS and FRPC relay is the path and Tailscale is never used |
| bot/docs/DECISIONS.md D3 versus PLAN.md 556 | Contradicting UI choices (lesson L18). One UI decision is recorded once (plan D18) |
| windows/ (85 files) | Windows port, not part of main. Spencer ditched the Windows branch as a base |
