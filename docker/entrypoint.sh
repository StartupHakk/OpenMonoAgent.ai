#!/usr/bin/env bash
# OpenMono agent container entrypoint.
#
# Data dir: $OPENMONO_DATA_DIR (default ~/.openmono, /home/agent/.openmono in
# this image). Launchers bind-mount the host's ~/.openmono there and run the
# container as --user <host uid:gid>.
# Behavior: probe DATA_DIR/sessions for writability. When unwritable, export
# OPENMONO_DATA_DIR=/tmp/openmono so the agent still starts; sessions and
# artifacts from that run do not persist. ConfigLoader repeats the same probe
# inside the agent before any writes.
set -euo pipefail

if ! id -un >/dev/null 2>&1; then
    echo "agent:x:$(id -u):$(id -g):agent:${HOME:-/home/agent}:/bin/bash" >> /etc/passwd 2>/dev/null || true
fi

DATA_DIR="${OPENMONO_DATA_DIR:-${HOME}/.openmono}"

# Writability probe: write a temp file under <dir>/sessions, the subdirectory
# the agent persists to. A writable top-level dir does not imply a writable
# sessions/ subdir.
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

# Exec the agent binary with the container args.
exec /usr/local/bin/openmono/openmono "$@"
