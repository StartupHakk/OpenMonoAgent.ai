# Step: Apply to Staging

Apply migrations to the staging database.
Record row counts before and after for every affected table.

## Instructions

1. Capture pre-migration row counts for all affected tables:

   ```bash
   bash {{playbook.base-path}}/scripts/row-counts.sh staging
   ```

2. Run the apply script against staging:

   ```bash
   bash {{playbook.base-path}}/scripts/apply.sh staging
   ```

3. If the script exits non-zero:
   - If `{{params.rollback-on-failure}}` is true: run the down migrations immediately
   - Report exactly which migration failed and why
   - Abort the playbook — do not proceed to smoke tests or prod

4. Capture post-migration row counts:

   ```bash
   bash {{playbook.base-path}}/scripts/row-counts.sh staging
   ```

## Output

```
Staging Migration
─────────────────
Started at  : <UTC timestamp>
Completed at: <UTC timestamp>
Result      : SUCCESS / FAILED

Migrations applied
──────────────────
✔  001_create_users.up.sql       (0.12s)
✔  002_add_email_index.up.sql    (0.34s)
✔  003_drop_old_sessions.up.sql  (0.08s)

Row count changes
─────────────────
users      : 10,482 → 10,482 (unchanged ✔)
sessions   : 45,291 → 0       (dropped ⚠ — expected for DROP TABLE)
```
