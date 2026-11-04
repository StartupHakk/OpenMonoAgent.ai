#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Example routine check: prints TODO lines from one workspace file. Exit 0
# with output means "something to look at"; exit 1 means quiet.
#   checks: [ { command: { run: "new-todo-lines.sh notes/todo.md" } } ]
set -euo pipefail
file="${1:-TODO.md}"
if [[ "$file" == /* ]] || [[ "$file" == *..* ]]; then
  echo "new-todo-lines: the path must stay inside the workspace" >&2
  exit 2
fi
[[ -f "/workspace/$file" ]] || exit 1
lines="$(grep -n "TODO" "/workspace/$file" | head -n 40 || true)"
[[ -n "$lines" ]] || exit 1
echo "$lines"
