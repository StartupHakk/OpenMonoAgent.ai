#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Pre tool hook for roster bots. Only the tool name is passed, never the tool
# input (OMA substitutes hook variables without quoting; builder choice B10).
# Exit 2 blocks the tool.
set -u
name="${1:-}"
if [[ ! "$name" =~ ^[A-Za-z0-9_]{1,80}$ ]]; then
  echo "roster: blocked a tool with an unexpected name" >&2
  exit 2
fi
list="${ROSTER_ALLOWED_TOOLS:-/omabot/bot/allowed-tools.txt}"
# Fail closed: no readable list means no tools.
if [[ ! -r "$list" ]]; then
  echo "roster: tool list missing, blocking $name" >&2
  exit 2
fi
if ! grep -qxF -- "$name" "$list"; then
  # A list entry ending in * (mcp__desktop__*) allows names with that prefix.
  allowed=0
  while IFS= read -r entry; do
    case "$entry" in
      mcp__*__\*) [[ "$name" == "${entry%\*}"* ]] && allowed=1 ;;
    esac
  done < "$list"
  if [[ $allowed -ne 1 ]]; then
    echo "roster: $name is not on this bot's tool list" >&2
    exit 2
  fi
fi
# The roster writes a gate before each turn it starts. A routine or consult
# turn on an edits or auto bot lists the changing tools it may not use.
gate="${ROSTER_TURN_GATE:-/omabot/bot/turn-gate}"
if [[ -r "$gate" ]] && grep -qxF -- "blocked=$name" "$gate"; then
  origin="$(sed -n 's/^origin=\([a-z_]*\)$/\1/p' "$gate" | head -n 1)"
  echo "roster: $name is not allowed in a ${origin:-gated} turn. Ask Spencer with the roster ask tool instead." >&2
  exit 2
fi
# Outward tools leave a marker before they run, so a turn cut by a restart
# is never repeated (plan 7.5 restart policy).
case "$name" in
  HostExec|Bash|Playbook|Agent|mcp__*)
    if [[ "$name" != mcp__roster__* ]]; then
      flags="${ROSTER_FLAGS_DIR:-/home/agent/.openmono/roster-flags}"
      { mkdir -p "$flags" && echo "$(date -u +%s) $name" >> "$flags/outward.log"; } 2>/dev/null || true
    fi
    ;;
esac
exit 0
