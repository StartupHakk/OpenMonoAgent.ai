# Step: Dry Run on Dev Database

Execute migrations against the dev database with `--dry-run` to capture the
full schema diff without committing any changes.

## Instructions

1. Run the dry-run script:

   ```bash
   bash {{playbook.base-path}}/scripts/dry-run.sh
   ```

2. Capture row counts for all tables that will be affected before any changes:

   ```bash
   # Example for PostgreSQL — adjust for your DB
   psql "$DATABASE_URL_DEV" -c "\dt" 2>/dev/null | head -30 || \
   mysql -u"$DB_USER" -p"$DB_PASSWORD" "$DB_NAME_DEV" -e "SHOW TABLES;" 2>/dev/null || \
   echo "Could not connect to dev DB — check DATABASE_URL_DEV or DB_* env vars"
   ```

3. Parse the dry-run output into a schema diff:
   - Tables added
   - Tables removed
   - Columns added / modified / removed
   - Indexes created / dropped
   - Constraints added / removed

## Output

```
Dry Run — Dev Database
──────────────────────
Migration path  : {{params.migration-path}}
Database        : dev

Schema Changes
──────────────
+ CREATE TABLE  sessions_new (id, user_id, created_at)
~ ALTER TABLE   users ADD COLUMN last_login_at TIMESTAMP
~ ALTER TABLE   users DROP COLUMN legacy_token        ← DESTRUCTIVE
- DROP TABLE    old_sessions                           ← DESTRUCTIVE

Destructive operations : <count>
Safe operations        : <count>

Pre-migration row counts
────────────────────────
users       : <count> rows
sessions    : <count> rows
<table>     : <count> rows
```
