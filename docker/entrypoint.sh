#!/usr/bin/env bash
# OpenMono agent container entrypoint wrapper.
#
# When the agent runs with --user (host UID:GID), the bind-mounted
# ~/.openmono directory may be owned by root or another user, making it
# unwritable. This entrypoint detects that situation and redirects
# OPENMONO_DATA_DIR to a writable temp directory so the agent can still
# function (artifacts/sessions just won't persist across container runs).
#
# The agent (ConfigLoader) performs the same check and self-heals, so this is
# belt-and-suspenders — but it must probe the same path the agent writes to.
set -euo pipefail

DATA_DIR="${OPENMONO_DATA_DIR:-${HOME}/.openmono}"

# Probe writability with a real file write inside the sessions/ subdir — the
# directory the agent actually persists into. The top-level dir can be
# writable while a host-pre-created sessions/ subdir is owned by another UID,
# so testing only the top level (as a plain `touch` would) misses the very
# case that crashes the agent with UnauthorizedAccessException.
probe_writable() {
    local dir="$1"
    mkdir -p "${dir}/sessions" 2>/dev/null || return 1
    local probe="${dir}/sessions/.writable-test.$$"
    touch "${probe}" 2>/dev/null || return 1
    rm -f "${probe}" 2>/dev/null || true
    return 0
}

if ! probe_writable "${DATA_DIR}"; then
    echo "[openmono-entrypoint] ${DATA_DIR} is not writable — redirecting data to /tmp/openmono" >&2
    DATA_DIR="/tmp/openmono"
    mkdir -p "${DATA_DIR}/sessions"
    export OPENMONO_DATA_DIR="${DATA_DIR}"
fi

# Execute the real openmono binary with whatever args were passed
exec /usr/local/bin/openmono/openmono "$@"
