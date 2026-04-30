#!/usr/bin/env bash
# db-migrate/scripts/apply.sh
# Applies migrations to the staging database.
set -euo pipefail

ENV="${1:-staging}"
MIGRATION_PATH="${MIGRATION_PATH:-./migrations}"
ROLLBACK_ON_FAILURE="${ROLLBACK_ON_FAILURE:-true}"

# Pick the right DATABASE_URL
if [ "$ENV" = "staging" ]; then
  DATABASE_URL="${DATABASE_URL_STAGING:-${DATABASE_URL:-}}"
else
  DATABASE_URL="${DATABASE_URL:-}"
fi

echo "Applying migrations — $ENV"
echo "──────────────────────────────────────────"

if [ -z "$DATABASE_URL" ]; then
  echo "ERROR: DATABASE_URL_STAGING is not set." >&2
  echo "Export it before running: export DATABASE_URL_STAGING='...' " >&2
  exit 1
fi

run_migration() {
  if command -v dbmate &>/dev/null; then
    dbmate --url "$DATABASE_URL" --migrations-dir "$MIGRATION_PATH" up
  elif command -v migrate &>/dev/null; then
    migrate -path "$MIGRATION_PATH" -database "$DATABASE_URL" up
  else
    echo "ERROR: No migration tool found (dbmate or golang-migrate required)." >&2
    exit 1
  fi
}

if run_migration; then
  echo ""
  echo "Migrations applied successfully to $ENV."
  exit 0
else
  EXIT=$?
  echo ""
  echo "ERROR: Migration failed on $ENV (exit $EXIT)." >&2

  if [ "$ROLLBACK_ON_FAILURE" = "true" ]; then
    echo "Rolling back..." >&2
    if command -v dbmate &>/dev/null; then
      dbmate --url "$DATABASE_URL" --migrations-dir "$MIGRATION_PATH" down || true
    elif command -v migrate &>/dev/null; then
      migrate -path "$MIGRATION_PATH" -database "$DATABASE_URL" down 1 || true
    fi
    echo "Rollback complete." >&2
  fi

  exit "$EXIT"
fi
