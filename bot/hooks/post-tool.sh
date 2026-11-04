#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Post tool hook after file writes. Leaves a flag so the roster rescans memory
# files for secrets at the end of the turn. Never reads tool output.
set -u
name="${1:-}"
if [[ ! "$name" =~ ^[A-Za-z0-9_]{1,80}$ ]]; then exit 0; fi
flags=/home/agent/.openmono/roster-flags
mkdir -p "$flags" 2>/dev/null || exit 0
date -u +%s > "$flags/rescan-memory" 2>/dev/null || true
exit 0
