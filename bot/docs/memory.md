# Per-bot memory (phase 3, M3)

Files live in the bot workspace:

| File | Loaded |
|---|---|
| MEMORY.md | every turn, first 200 lines or 24 KB, rendered into OPENMONO.md |
| memory/TOPIC.md | on demand (`memory_read_topic`) and by recall |
| memory/archive.md | never; entries moved out of MEMORY.md, marked `· moved DATE` |
| memory/log/DATE.md | never; one line per turn, for Spencer |

Entry format: `- 2026-11-02 · from chat · fact [· until 2026-12-31]`.

## Rules the code enforces

- Every write is redacted, checked against the revision the writer read (409 stale, 428 missing), kept inside the budget (struck, then expired, then oldest live entries move to the archive; archive written first) and written atomically with mode 0600.
- Undated lines and health or safety facts (decision B25) never move.
- Every change writes a journal row in roster.db and bots/BOT/journal/. `oma-bot memory undo BOT ID` restores the before state only if the file has not changed since.
- The roster never follows a symlink inside the memory paths.

## Bot tools (MCP shim)

`memory_read`, `memory_update`, `memory_read_topic`, `memory_forget`, plus `list_bots`. The shim fills the revision for the bot and retries once on a conflict.

## Upkeep (roster service, background priority)

- Capture after 2 quiet minutes or 6 turns; a fact is kept only if its quote appears in what Spencer wrote.
- Organize past half the budget, at most 20 lines per pass.
- Nightly tidy after 3:00 in the bot's zone: expired out, exact duplicates removed, contradictions struck (at most a fifth, none under 5 entries).
- Forget records the fact so capture never adds it back.

## Commands

```
oma-bot memory show BOT
oma-bot memory journal BOT [--lines N]
oma-bot memory undo BOT ID
oma-bot memory forget BOT "fact" [--topic T]
oma-bot memory scan BOT
oma-bot memory tidy BOT
```
