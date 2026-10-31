#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — shared version-pin resolver.
#
# Two files drive what `openmono upgrade` installs:
#
#   $REPO_DIR/versions.json          the catalog of default pins (shipped in the
#                                    repo; bumping a pin is a one-line edit here).
#   ~/.openmono/versions.json        the per-box manifest (created on install/
#                                    upgrade). It lists ONLY what is actually
#                                    installed (or what the user has opted into).
#                                    Each component key is an object:
#                                    { "version": <concrete ref>, "status":
#                                    <latest|pinned> }.
#
# Box-file semantics:
#   - `version` is ALWAYS the concrete ref actually installed on the box — never
#     the literal string "latest". It is written with the real, per-OS-probed
#     ref after the component is installed, and re-probed (self-healed) on
#     upgrade.
#   - `status` is the sole control switch:
#       "latest" -> "follow the catalog." On upgrade the target is the repo
#         versions.json pin; if the installed version differs it is refreshed and
#         the new concrete version recorded (status stays "latest").
#       "pinned" -> "use exactly this." The box `version` is the target; upgrade
#         uses/keeps it and does not chase the catalog.
#   - A component that is not installed is simply ABSENT from the file (no
#     "latest" marker), so the file doubles as the "what's installed / what the
#     user wants" manifest. Pre-declaring an absent key opts the user into
#     installing it.
#
# Sourced by the openmono CLI and scripts (setup-tunnel-inference.sh,
# install.sh). All symbols are VP_-prefixed to avoid clobbering the host's
# shell.
# ──────────────────────────────────────────────────────────────────────────────

# Component identifiers. `agent` and `strata` are local components that have no
# image in the repo catalog: agent is rebuilt from the pulled code, strata is a
# host-native binary. They appear in the box file only when present (or when the
# user has opted into them).
VP_COMPONENTS="frp llama strata agent caddy searxng scrapling"

# The repo root as seen from this lib file (scripts/lib/ -> repo root).
VP_REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../" && pwd)"
VP_CATALOG="$VP_REPO_DIR/versions.json"

# Resolve the box manifest-file path on every call, so OPENMONO_VERSIONS_FILE
# (used by tests / overrides) always wins even when set after this lib is
# sourced.
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

# ── box manifest reads (~/.openmono/versions.json) ────────────────────────────

# The raw value recorded for a component (always a concrete ref, or empty when
# the component/file is absent).
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

# 0 when the component is locked by the user: status is "pinned".
# A concrete version string alone does NOT lock a component — the reconciler
# records the installed ref for every auto-refreshed component, keeping
# status "latest" so it keeps tracking the catalog. Only an explicit user pin
# (status "pinned") causes `openmono upgrade` to skip the component.
vp_box_locked() {
    [[ "$(vp_box_status "$1")" == "pinned" ]]
}

# 0 when a key is present in the box file (i.e. the component is installed, or
# the user has opted into installing it). Absent components are simply not in
# the file — the file is the "what's installed / what's wanted" manifest.
vp_box_present() {
    local comp="$1" f
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || return 1
    jq -e --arg c "$comp" '(.[$c] // null) != null' "$f" >/dev/null 2>&1
}

# The effective ref to install for a component — the single "what should we
# install" query. A user pin (status "pinned") wins: the box value is the
# target. Otherwise the catalog default is the target; an auto-recorded
# concrete version (status "latest") is informational only and never overrides
# the catalog.
#   $1 = catalog key (frp | llama_cpu | caddy | ...), $2 = box component id
#   (for locking; defaults to the catalog key).
vp_box_effective() {
    local key="$1" comp="${2:-$1}"
    if vp_box_locked "$comp"; then
        printf '%s' "$(vp_box_version "$comp")"
        return 0
    fi
    if [[ "$key" == "frp" ]]; then
        vp_catalog_frp
    else
        vp_catalog_image "$key"
    fi
}

# Effective frp version to install (thin wrapper kept for existing callers).
vp_effective_frp() {
    vp_box_effective frp frp
}

# Effective docker image ref for a catalog key, honoring a user lock.
# $1 = catalog key (llama_cpu|caddy|...), $2 = box component id (for locking).
vp_effective_image() {
    local key="$1" comp="${2:-$1}"
    vp_box_effective "$key" "$comp"
}

# ── box manifest writes ──────────────────────────────────────────────────────

# Ensure the box manifest file exists. Absent components are NOT seeded — the
# file lists only what is actually installed (or opted into). Keys that ARE
# present are normalized: a bare string becomes an object (a concrete string is
# a user pin -> status "pinned"; "latest" -> status "latest"), and an object
# gets a default status. Safe to call on every install/upgrade.
vp_box_ensure() {
    command -v jq &>/dev/null || return 0
    local f comp tmp existing
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || { mkdir -p "$(dirname "$f")"; echo '{}' > "$f"; }
    for comp in $VP_COMPONENTS; do
        # Only normalize keys that already exist; do not add missing ones.
        existing="$(jq -r --arg c "$comp" 'if (.[$c] // null) == null then empty else "y" end' "$f" 2>/dev/null || true)"
        [[ "$existing" == "y" ]] || continue
        tmp="$f.tmp"
        if jq --arg c "$comp" '
            .[$c] = (if (.[$c] | type) == "string"
                     then { version: .[$c], status: (if .[$c] == "latest" then "latest" else "pinned" end) }
                     else (.[$c] + { status: (.[$c].status // "latest") }) end)
        ' "$f" > "$tmp" 2>/dev/null; then
            mv "$tmp" "$f"
        else
            rm -f "$tmp"
        fi
    done
    chmod 0600 "$f" 2>/dev/null || true
}

# Record the concrete ref actually installed for a component, PRESERVING its
# status (a pinned component stays pinned, a latest one stays latest). This is
# the single writer used after a successful install/refresh, so the file always
# shows what is really on the box while the user's lock intent is untouched.
vp_box_record_installed() {
    local comp="$1" ver="$2" f
    command -v jq &>/dev/null || return 0
    [[ -n "$ver" ]] || return 0
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || { mkdir -p "$(dirname "$f")"; echo '{}' > "$f"; }
    local tmp="$f.tmp"
    if jq --arg c "$comp" --arg v "$ver" '
        .[$c] = (if (.[$c] // null) == null then { version: $v, status: "latest" }
                 elif (.[$c] | type) == "string" then { version: $v, status: (if $v == "latest" then "latest" else "pinned" end) }
                 else (.[$c] + { version: $v }) end)
    ' "$f" > "$tmp" 2>/dev/null; then
        mv "$tmp" "$f"; chmod 0600 "$f" 2>/dev/null || true
    else
        rm -f "$tmp"
    fi
}

# Drop a component key from the box file (self-heal when a component is no
# longer present and was never user-declared). No-op when the key is absent.
vp_box_remove() {
    local comp="$1" f
    command -v jq &>/dev/null || return 0
    f="$(_vp_box_file)"
    [[ -f "$f" ]] || return 0
    vp_box_present "$comp" || return 0
    local tmp="$f.tmp"
    if jq --arg c "$comp" 'del(.[ $c ])' "$f" > "$tmp" 2>/dev/null; then
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
    [[ -f "$f" ]] || { mkdir -p "$(dirname "$f")"; echo '{}' > "$f"; }
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
