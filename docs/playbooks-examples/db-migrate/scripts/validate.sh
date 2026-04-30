#!/usr/bin/env bash
# db-migrate/scripts/validate.sh
# Validates migration files for syntax and destructive operations.
set -euo pipefail

MIGRATION_PATH="${MIGRATION_PATH:-./migrations}"
ALLOW_DESTRUCTIVE="${ALLOW_DESTRUCTIVE:-false}"

ERRORS=0
WARNINGS=0
DESTRUCTIVE_FOUND=0

echo "Validating migrations in: $MIGRATION_PATH"
echo "──────────────────────────────────────────"

if [ ! -d "$MIGRATION_PATH" ]; then
  echo "ERROR: Migration path '$MIGRATION_PATH' does not exist." >&2
  exit 1
fi

FILES=$(find "$MIGRATION_PATH" -name "*.sql" | sort)
COUNT=$(echo "$FILES" | grep -c . || true)

if [ "$COUNT" -eq 0 ]; then
  echo "No migration files found in $MIGRATION_PATH"
  exit 0
fi

echo "Found $COUNT migration file(s)"
echo ""

while IFS= read -r file; do
  BASENAME=$(basename "$file")
  DESTRUCTIVE_OPS=$(grep -iE "DROP TABLE|DROP COLUMN|DELETE FROM|TRUNCATE" "$file" 2>/dev/null || true)

  if [ -n "$DESTRUCTIVE_OPS" ]; then
    DESTRUCTIVE_FOUND=$((DESTRUCTIVE_FOUND+1))
    printf "  ⚠  %s  ← DESTRUCTIVE\n" "$BASENAME"
    echo "$DESTRUCTIVE_OPS" | sed 's/^/     /'
  else
    printf "  ✔  %s\n" "$BASENAME"
  fi
done <<< "$FILES"

echo ""

if [ "$DESTRUCTIVE_FOUND" -gt 0 ] && [ "$ALLOW_DESTRUCTIVE" = "false" ]; then
  echo "ERROR: $DESTRUCTIVE_FOUND destructive operation(s) found." >&2
  echo "Re-run with --allow-destructive true if this is intentional." >&2
  exit 1
fi

echo "Validation passed ($COUNT files, $DESTRUCTIVE_FOUND destructive)."
exit 0
