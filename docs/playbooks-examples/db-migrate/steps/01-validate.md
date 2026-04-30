# Step: Validate Migration Files

Parse all pending migration files and flag issues before touching any database.

## Instructions

1. List all migration files in order:

   ```bash
   find {{params.migration-path}} -name "*.sql" -o -name "*.up.sql" | sort
   ```

2. For each migration file, check:
   - Valid SQL syntax (no obvious parse errors)
   - Presence of destructive operations: `DROP TABLE`, `DROP COLUMN`, `DELETE`, `TRUNCATE`, `ALTER TABLE ... DROP`
   - Presence of a corresponding down migration (`*.down.sql`)

3. If `{{params.allow-destructive}}` is `false` and any destructive operation is found:
   **Abort immediately** — list each file and the destructive operation found.
   Tell the user to re-run with `--allow-destructive true` if intentional.

4. Check migration sequence for gaps (e.g. 001, 002, 004 — missing 003):

   ```bash
   find {{params.migration-path}} -name "*.sql" | sort | \
     grep -oE '[0-9]+' | head -1
   ```

## Output

```
Migration Validation
────────────────────
Path            : {{params.migration-path}}
Files found     : <count>
Allow destructive: {{params.allow-destructive}}

Files
─────
✔  001_create_users.up.sql          (safe)
✔  002_add_email_index.up.sql       (safe)
⚠  003_drop_old_sessions.up.sql     (DESTRUCTIVE: DROP TABLE)
✔  004_add_created_at.up.sql        (safe)

Issues
──────
<list any destructive ops, syntax errors, missing down migrations, or sequence gaps>

Verdict: <PASS / BLOCKED>
```
