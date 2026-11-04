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
  echo "roster: $name is not on this bot's tool list" >&2
  exit 2
fi
exit 0
