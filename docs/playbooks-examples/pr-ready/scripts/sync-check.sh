#!/usr/bin/env bash
# pr-ready/scripts/sync-check.sh
# Verifies the branch has commits ahead of target. Exit 1 if nothing to PR.
set -euo pipefail

TARGET="${TARGET_BRANCH:-main}"

git fetch origin "$TARGET" --quiet 2>/dev/null || true

AHEAD=$(git rev-list --count "origin/$TARGET..HEAD" 2>/dev/null || echo 0)
BEHIND=$(git rev-list --count "HEAD..origin/$TARGET" 2>/dev/null || echo 0)
BRANCH=$(git symbolic-ref --short HEAD 2>/dev/null || echo "detached")

echo "Branch  : $BRANCH"
echo "Target  : $TARGET"
echo "Ahead   : $AHEAD commit(s)"
echo "Behind  : $BEHIND commit(s)"

if [ "$BRANCH" = "$TARGET" ]; then
  echo "ERROR: Cannot open a PR from '$TARGET' itself." >&2
  exit 1
fi

if [ "$AHEAD" -eq 0 ]; then
  echo "ERROR: Branch has no commits ahead of $TARGET — nothing to PR." >&2
  exit 1
fi

if [ "$BEHIND" -gt 0 ]; then
  echo "WARNING: Branch is $BEHIND commit(s) behind $TARGET — consider rebasing."
fi

exit 0
