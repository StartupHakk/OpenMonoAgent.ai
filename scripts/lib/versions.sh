#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — shared version-pin resolver.
#
# Two files drive what `openmono upgrade` installs:
#
#   $REPO_DIR/versions.json          the catalog of default pins (shipped in the
#                                    repo; bumping a pin is a one-line edit here).
#   ~/.openmono/versions.json        the per-box lock file (created on first
#                                    install/upgrade). Every component is an
#                                    object: { "version": <value>, "status":
#                                    <latest|pinned> }.
#
# Resolution, per component:
#   1. read the box lock file;
#      - status "pinned" (or a non-"latest" version) -> LOCKED: upgrade keeps
#        the existing version and never touches the component;
#      - status "latest" (default) -> use the repo versions.json value, so the
#        component tracks updates.
#
# Sourced by the openmono CLI and scripts (setup-tunnel-inference.sh,
# install.sh). All symbols are VP_-prefixed to avoid clobbering the host's
# shell.
# ──────────────────────────────────────────────────────────────────────────────

# Component identifiers. `agent` and `strata` are local components that have no
# image in the repo catalog: agent is rebuilt from the pulled code, strata is a
# host-native binary. They appear in the box lock file so a user can still pin
# them (e.g. "don't refresh Strata on upgrade").
VP_COMPONENTS="frp llama strata agent caddy searxng scrapling"

# The repo root as seen from this lib file (scripts/lib/ -> repo root).
VP_REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../" && pwd)"
VP_CATALOG="$VP_REPO_DIR/versions.json"

# Resolve the box lock-file path on every call, so OPENMONO_VERSIONS_FILE (used
# by tests / overrides) always wins even when set after this lib is sourced.
_vp_box_file() {
    printf '%s' "${OPENMONO_VERSIONS_FILE:-$HOME/.openmono/versions.json}"
}

# ── catalog reads (repo versions.json) ────────────────────────────────────────

# Default frp version from the repo catalog. Empty when the catalog is absent.
vp_catalog_frp() {
    [[ -f "$VP_CATALOG" ]] || return 0
    jq -r '.frp // empty' "$VP_CATALOG" 2>/dev/null || true
}

# Default docker image ref for a catalog key (llama_cpu|llama_cuda|
# llama_vulkan|caddy|searxng|scrapling).
vp_catalog_image() {
    local key="$1"
    [[ -f "$VP_CATALOG" ]] || return 0
    jq -r --arg k "$key" '.images[$k] // empty' "$VP_CATALOG" 2>/dev/null || true
}

# ── box lock-file reads (~/.openmono/versions.json) ───────────────────────────

# The raw value recorded for a component: "latest" or a pinned string.
# Returns empty when the component (or file) is absent.
vp_box_version() {
    local comp="$1" f
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || return 0
    jq -r --arg c "$comp" '(.[$c] // {}) | if type == "string" then . else .version // empty end' \
        "$f" 2>/dev/null || true
}

# The recorded status for a component (latest|pinned); defaults to latest.
vp_box_status() {
    local comp="$1" f
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || return 0
    local s
    s="$(jq -r --arg c "$comp" '(.[$c] // {}) | if type == "object" then .status // "latest" else "latest" end' \
        "$f" 2>/dev/null || true)"
    printf '%s' "${s:-latest}"
}

# 0 when the component is locked: status "pinned" OR a non-"latest" version
# string. Locked components are left untouched by `openmono upgrade`.
vp_box_locked() {
    local comp="$1" v
    v="$(vp_box_version "$comp")"
    if [[ -n "$v" && "$v" != "latest" ]]; then
        return 0
    fi
    [[ "$(vp_box_status "$comp")" == "pinned" ]]
}

# Effective frp version to install: locked box value, else catalog default.
vp_effective_frp() {
    local v
    v="$(vp_box_version frp)"
    if [[ -n "$v" && "$v" != "latest" ]]; then
        printf '%s' "$v"
        return 0
    fi
    vp_catalog_frp
}

# Effective docker image ref for a catalog key, honoring a box lock.
# $1 = catalog key (llama_cpu|caddy|...), $2 = box component id (for locking).
vp_effective_image() {
    local key="$1" comp="${2:-$1}" v
    v="$(vp_box_version "$comp")"
    if [[ -n "$v" && "$v" != "latest" ]]; then
        printf '%s' "$v"
        return 0
    fi
    vp_catalog_image "$key"
}

# ── box lock-file writes ──────────────────────────────────────────────────────

# Ensure the box lock file exists, seeding every component to
# { version: "latest", status: "latest" } without clobbering values the user has
# already set (including pinned ones). Safe to call on every install/upgrade.
vp_box_ensure() {
    command -v jq &>/dev/null || return 0
    local f comp tmp
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || { mkdir -p "$(dirname "$f")"; echo '{}' > "$f"; }
    for comp in $VP_COMPONENTS; do
        tmp="$f.tmp"
        if jq --arg c "$comp" '
            .[$c] = (if (.[$c] // null) == null then { version: "latest", status: "latest" }
                     elif (.[$c] | type) == "string" then { version: .[$c], status: (if .[$c] == "latest" then "latest" else "pinned" end) }
                     else (.[$c] + { version: (.[$c].version // "latest"), status: (.[$c].status // "latest") }) end)
        ' "$f" > "$tmp" 2>/dev/null; then
            mv "$tmp" "$f"
        else
            rm -f "$tmp"
        fi
    done
    chmod 0600 "$f" 2>/dev/null || true
}

# Record the version actually installed for a component (e.g. the frpc version
# after a refresh). A recorded concrete version is marked "pinned" so the user
# can see exactly what is on the box; "latest" is left untouched.
vp_box_record() {
    local comp="$1" ver="$2" f
    command -v jq &>/dev/null || return 0
    [[ -n "$ver" ]] || return 0
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || vp_box_ensure
    local tmp="$f.tmp"
    if jq --arg c "$comp" --arg v "$ver" '
        .[$c] = (if (.[$c] // null) == null then { version: $v, status: (if $v == "latest" then "latest" else "pinned" end) }
                 elif (.[$c] | type) == "string" then { version: $v, status: (if $v == "latest" then "latest" else "pinned" end) }
                 else (.[$c] + { version: $v, status: (if $v == "latest" then (.[$c].status // "latest") else "pinned" end) }) end)
    ' "$f" > "$tmp" 2>/dev/null; then
        mv "$tmp" "$f"; chmod 0600 "$f" 2>/dev/null || true
    else
        rm -f "$tmp"
    fi
}

# Record the version actually installed for a component but KEEP its status as
# "latest" (i.e. it still tracks the repo catalog pin). Used when the reconciler
# auto-refreshes a component to the current catalog pin: we want to show what is
# on the box, but the user did not pin it, so a future upgrade must still track
# the catalog. (Contrast vp_box_record, which marks a concrete value "pinned"
# and would therefore lock the component forever.)
vp_box_record_latest() {
    local comp="$1" ver="$2" f
    command -v jq &>/dev/null || return 0
    [[ -n "$ver" ]] || return 0
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || vp_box_ensure
    local tmp="$f.tmp"
    if jq --arg c "$comp" --arg v "$ver" '
        .[$c] = (if (.[$c] // null) == null then { version: $v, status: "latest" }
                 elif (.[$c] | type) == "string" then { version: $v, status: "latest" }
                 else (.[$c] + { version: $v, status: (.[$c].status // "latest") }) end)
    ' "$f" > "$tmp" 2>/dev/null; then
        mv "$tmp" "$f"; chmod 0600 "$f" 2>/dev/null || true
    else
        rm -f "$tmp"
    fi
}

# Record the install role (full|inference|agent) in the box file.
vp_box_set_role() {
    local role="$1" f
    command -v jq &>/dev/null || return 0
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || vp_box_ensure
    local tmp="$f.tmp"
    if jq --arg r "$role" '.role = $r' "$f" > "$tmp" 2>/dev/null; then
        mv "$tmp" "$f"; chmod 0600 "$f" 2>/dev/null || true
    else
        rm -f "$tmp"
    fi
}

# The recorded install role (full|inference|agent); empty when unknown.
vp_box_role() {
    local f
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || return 0
    jq -r '.role // empty' "$f" 2>/dev/null || true
}
