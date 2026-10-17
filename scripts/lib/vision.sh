#!/usr/bin/env bash
# Shared helper for the installers: persist the vision (image-input) choice to
# ~/.openmono/settings.json under inference.vision_enabled.
#
# This is the single source of truth for whether the agent accepts images —
# ConfigLoader merges it into AppConfig, and because ~/.openmono is bind-mounted
# into every agent container (extension, host-bridge, and compose launches all
# mount it and set OPENMONO_DATA_DIR), the setting reaches every launch path
# without any per-launcher env wiring. The OPENMONO_VISION_ENABLED env var still
# works for one-off overrides; the settings file wins when set.
#
# Usage: om_persist_vision <true|false> [settings_path]

om_persist_vision() {
    local flag="$1"
    local settings="${2:-$HOME/.openmono/settings.json}"
    if ! command -v jq &>/dev/null; then
        # jq missing: the agent falls back to OPENMONO_VISION_ENABLED (written
        # to docker/.env by the installers), so this is best-effort.
        return 0
    fi
    mkdir -p "$(dirname "$settings")"
    [[ -f "$settings" ]] || echo '{}' > "$settings"
    if jq --argjson v "$flag" '.inference.vision_enabled = $v' \
        "$settings" > "${settings}.tmp" 2>/dev/null; then
        mv "${settings}.tmp" "$settings"
        chmod 0600 "$settings" 2>/dev/null || true
    else
        rm -f "${settings}.tmp"
    fi
}
