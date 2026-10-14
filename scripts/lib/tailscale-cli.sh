#!/usr/bin/env bash
# Resolve the Tailscale CLI binary (PATH or macOS Tailscale.app).
# Usage: source this file, then: resolve_tailscale_cli || return 1
# Sets: TAILSCALE_BIN
#
# Helpers:
#   tailscale_cmd <args...>     — run CLI
#   tailscale_backend_state     — prints BackendState (or empty)
#   tailscale_is_running        — 0 if BackendState=Running
#   tailscale_login_hint        — prints login URL / next steps on stderr when logged out

resolve_tailscale_cli() {
    TAILSCALE_BIN=""

    if command -v tailscale &>/dev/null; then
        TAILSCALE_BIN="$(command -v tailscale)"
        return 0
    fi

    # GUI install on macOS often leaves CLI off PATH
    local candidates=(
        "/Applications/Tailscale.app/Contents/MacOS/Tailscale"
        "/Applications/Tailscale.app/Contents/MacOS/tailscale"
    )
    local c
    for c in "${candidates[@]}"; do
        if [[ -x "$c" ]]; then
            TAILSCALE_BIN="$c"
            return 0
        fi
    done

    return 1
}

tailscale_cmd() {
    if [[ -z "${TAILSCALE_BIN:-}" ]]; then
        resolve_tailscale_cli || return 1
    fi
    "$TAILSCALE_BIN" "$@"
}

# Capture stderr+stdout; Tailscale macOS often prints "Logged out." / login URL on stdout.
tailscale_cmd_capture() {
    if [[ -z "${TAILSCALE_BIN:-}" ]]; then
        resolve_tailscale_cli || return 1
    fi
    "$TAILSCALE_BIN" "$@" 2>&1
}

tailscale_backend_state() {
    local out st
    out="$(tailscale_cmd_capture status --json 2>/dev/null)" || true
    if [[ -z "$out" ]]; then
        printf ''
        return 1
    fi
    # Prefer JSON BackendState when present
    if command -v jq &>/dev/null && echo "$out" | jq -e . >/dev/null 2>&1; then
        st="$(echo "$out" | jq -r '.BackendState // empty' 2>/dev/null || true)"
        printf '%s' "$st"
        [[ -n "$st" ]]
        return $?
    fi
    # Text fallbacks (CLI before login / prefs issues)
    if echo "$out" | grep -qi 'Failed to load preferences'; then
        printf 'PrefsError'
        return 1
    fi
    if echo "$out" | grep -qi 'Logged out'; then
        printf 'NeedsLogin'
        return 1
    fi
    printf ''
    return 1
}

tailscale_is_running() {
    local st
    st="$(tailscale_backend_state 2>/dev/null || true)"
    [[ "$st" == "Running" ]]
}

tailscale_login_hint() {
    local out url
    out="$(tailscale_cmd_capture status 2>/dev/null || true)"
    url="$(echo "$out" | grep -Eo 'https://login\.tailscale\.com/[^[:space:]]+' | head -1 || true)"
    if [[ -n "$url" ]]; then
        echo "Tailscale is logged out. Sign in:" >&2
        echo "  $url" >&2
        echo "Or open the Tailscale app menu → Log in." >&2
        return 0
    fi
    if echo "$out" | grep -qi 'Failed to load preferences'; then
        echo "Tailscale CLI cannot load preferences." >&2
        echo "  1. Open /Applications/Tailscale.app" >&2
        echo "  2. Complete login in the menu bar app" >&2
        echo "  3. Re-run: openmono tunnel status" >&2
        return 0
    fi
    echo "Tailscale is not connected. Open Tailscale.app and sign in, then:" >&2
    echo "  openmono tunnel setup --tailscale" >&2
}
