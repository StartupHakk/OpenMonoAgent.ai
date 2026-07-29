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

if ! id -un >/dev/null 2>&1; then
    echo "agent:x:$(id -u):$(id -g):agent:${HOME:-/home/agent}:/bin/bash" >> /etc/passwd 2>/dev/null || true
fi

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

probe_writable_existing() {
    local dir="$1"
    local probe="${dir}/.openmono-writable-test.$$"
    ( : > "${probe}" ) 2>/dev/null || return 1
    rm -f "${probe}" 2>/dev/null || true
    return 0
}

WORKSPACE_DIR="${OPENMONO_WORKSPACE:-/workspace}"
if [[ -d "${WORKSPACE_DIR}" ]] && ! probe_writable_existing "${WORKSPACE_DIR}"; then
    echo "[openmono-entrypoint] ${WORKSPACE_DIR} is not writable by this container user ($(id -u):$(id -g))." >&2
    echo "[openmono-entrypoint] The agent needs write access here to run commands like mkdir, edit files, etc." >&2
    echo "[openmono-entrypoint] This commonly happens under WSL2 + Docker Desktop when the project lives on a" >&2
    echo "[openmono-entrypoint] Windows drive (e.g. /mnt/c/...) — Docker Desktop's file-sharing layer for those" >&2
    echo "[openmono-entrypoint] paths doesn't always honor the container's mapped user/group." >&2
    echo "[openmono-entrypoint] Fix options:" >&2
    echo "[openmono-entrypoint]   1. Move the project into the WSL Linux filesystem (e.g. ~/projects/...) and run openmono from there." >&2
    echo "[openmono-entrypoint]   2. Or, from the host, run: chown -R \$(id -u):\$(id -g) <path-to-project>" >&2
    exit 1
fi

# Execute the real openmono binary with whatever args were passed
exec /usr/local/bin/openmono/openmono "$@"
