#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Routine check helper: fetch one branch of the repository mounted at
# /workspace. Runs only inside a throwaway check container on the roster
# network behind the egress gate, never on the host. Repository hooks and
# fsmonitor are turned off so a repository config cannot run commands.
set -euo pipefail
branch="${1:-}"
if [[ ! "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,120}$ ]] || [[ "$branch" == *..* ]]; then
  echo "git-fetch: not a plain branch name" >&2
  exit 2
fi
cd /workspace
exec git -c core.hooksPath=/dev/null -c core.fsmonitor=false -c protocol.file.allow=never \
  fetch --quiet --no-tags --no-recurse-submodules origin "refs/heads/${branch}:refs/remotes/origin/${branch}"
