#!/usr/bin/env bash
# shellcheck disable=SC2034  # TWEAK_* metadata is read by oma-7940hs.sh
# Tweak: let the service user open the iGPU (render + video groups).
#
# Without this, llama-server silently falls back to CPU (about 3 to 4 tok/s,
# GPU idle). The GPU guard in the service catches it, this tweak prevents it.

TWEAK_ID="gpu-access"
TWEAK_DESC="Service user in the render and video groups"
TWEAK_TAG="PROVEN"
TWEAK_REBOOT=0

tweak_check() {
    local g missing=""
    for g in render video; do
        id -nG "$OMA_SERVICE_USER" 2>/dev/null | tr ' ' '\n' | grep -qx "$g" || missing="$missing $g"
    done
    if [[ -z "$missing" ]]; then
        echo "active: $OMA_SERVICE_USER is in render, video"; return 0
    fi
    echo "missing groups:$missing"; return 1
}

tweak_apply() {
    local g added=""
    for g in render video; do
        getent group "$g" >/dev/null || { warn "Group $g does not exist on this system"; continue; }
        if ! id -nG "$OMA_SERVICE_USER" | tr ' ' '\n' | grep -qx "$g"; then
            run as_root usermod -aG "$g" "$OMA_SERVICE_USER"
            added="$added $g"
        fi
    done
    # Keep earlier additions on re-runs; only append what this run added.
    local prev; prev="$(state_get GROUPS_ADDED)"
    state_set GROUPS_ADDED "$(echo "$prev $added" | tr ' ' '\n' | sed '/^$/d' | sort -u | xargs)"
    ok "GPU access: $OMA_SERVICE_USER groups now: $(id -nG "$OMA_SERVICE_USER" | tr ' ' '\n' | grep -xE 'render|video' | xargs)"
}

tweak_revert() {
    local g
    for g in $(state_get GROUPS_ADDED); do
        run as_root gpasswd -d "$OMA_SERVICE_USER" "$g" || true
    done
    state_del GROUPS_ADDED
    ok "GPU access: removed only the groups install added"
}
