#!/usr/bin/env bash
# scan.sh — greps the workspace for TODO/FIXME comments and returns results
set -euo pipefail

WORKSPACE="${OPENMONO_WORKSPACE:-.}"
PATTERN="${SCAN_PATTERN:-TODO|FIXME|HACK|XXX}"
MAX_RESULTS="${SCAN_MAX:-50}"

echo "=== Grep Scan Results ==="
echo "Pattern : $PATTERN"
echo "Root    : $WORKSPACE"
echo ""

# Run grep across source files, excluding build artefacts and the playbook dir
RESULTS=$(grep -rn \
    --include="*.cs" \
    --include="*.md" \
    --include="*.json" \
    --include="*.sh" \
    --include="*.yaml" \
    --include="*.yml" \
    --exclude-dir=".git" \
    --exclude-dir="bin" \
    --exclude-dir="obj" \
    --exclude-dir=".openmono" \
    -E "$PATTERN" \
    "$WORKSPACE" 2>/dev/null || true)

if [[ -z "$RESULTS" ]]; then
    echo "No matches found."
    exit 0
fi

MATCH_COUNT=$(echo "$RESULTS" | wc -l | tr -d ' ')
FILE_COUNT=$(echo "$RESULTS" | cut -d: -f1 | sort -u | wc -l | tr -d ' ')

echo "Matches : $MATCH_COUNT"
echo "Files   : $FILE_COUNT"
echo ""
echo "--- Top results ---"
echo "$RESULTS" | head -"$MAX_RESULTS"
