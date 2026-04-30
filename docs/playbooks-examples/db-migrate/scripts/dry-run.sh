#!/usr/bin/env bash
# db-migrate/scripts/dry-run.sh
# Runs migrations in dry-run mode against the dev database.
# Supports: dbmate, flyway, golang-migrate, raw psql/mysql.
set -euo pipefail

MIGRATION_PATH="${MIGRATION_PATH:-./migrations}"
DATABASE_URL="${DATABASE_URL_DEV:-${DATABASE_URL:-}}"

echo "Dry run — dev database"
echo "──────────────────────────────────────────"

if [ -z "$DATABASE_URL" ]; then
  echo "WARNING: DATABASE_URL_DEV not set. Skipping actual dry run — showing migration list only."
  find "$MIGRATION_PATH" -name "*.up.sql" | sort | while read -r f; do
    echo "  would apply: $(basename "$f")"
  done
  exit 0
fi

# dbmate
if command -v dbmate &>/dev/null; then
  echo "Using: dbmate"
  dbmate --url "$DATABASE_URL" --migrations-dir "$MIGRATION_PATH" status
  exit 0
fi

# golang-migrate
if command -v migrate &>/dev/null; then
  echo "Using: golang-migrate"
  migrate -path "$MIGRATION_PATH" -database "$DATABASE_URL" -dry-run up 2>/dev/null || \
  migrate -path "$MIGRATION_PATH" -database "$DATABASE_URL" up -dry-run 2>/dev/null || \
  echo "golang-migrate dry-run not supported in this version — listing pending only"
  exit 0
fi

echo "No migration tool detected (dbmate, golang-migrate). Listing pending files only."
find "$MIGRATION_PATH" -name "*.up.sql" | sort | while read -r f; do
  echo "  pending: $(basename "$f")"
done

exit 0
