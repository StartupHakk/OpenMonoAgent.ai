#!/usr/bin/env bash
# db-migrate/scripts/apply-prod.sh
# Applies migrations to production. Requires DATABASE_URL_PROD to be set.
set -euo pipefail

MIGRATION_PATH="${MIGRATION_PATH:-./migrations}"
ROLLBACK_ON_FAILURE="${ROLLBACK_ON_FAILURE:-true}"
DATABASE_URL="${DATABASE_URL_PROD:-}"

echo "Applying migrations — PRODUCTION"
echo "──────────────────────────────────────────"

if [ -z "$DATABASE_URL" ]; then
  echo "ERROR: DATABASE_URL_PROD is not set." >&2
  echo "Export it: export DATABASE_URL_PROD='postgres://...'" >&2
  exit 1
fi

run_migration() {
  if command -v dbmate &>/dev/null; then
    dbmate --url "$DATABASE_URL" --migrations-dir "$MIGRATION_PATH" up
  elif command -v migrate &>/dev/null; then
    migrate -path "$MIGRATION_PATH" -database "$DATABASE_URL" up
  else
    echo "ERROR: No migration tool found." >&2
    exit 1
  fi
}

if run_migration; then
  echo ""
  echo "Production migrations applied successfully."
  exit 0
else
  EXIT=$?
  echo "ERROR: Production migration failed (exit $EXIT)." >&2

  if [ "$ROLLBACK_ON_FAILURE" = "true" ]; then
    echo "CRITICAL: Rolling back production..." >&2
    if command -v dbmate &>/dev/null; then
      dbmate --url "$DATABASE_URL" --migrations-dir "$MIGRATION_PATH" down || true
    elif command -v migrate &>/dev/null; then
      migrate -path "$MIGRATION_PATH" -database "$DATABASE_URL" down 1 || true
    fi
    echo "Rollback complete." >&2
  fi

  exit "$EXIT"
fi
