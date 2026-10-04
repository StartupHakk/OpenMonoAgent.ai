#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — install the bare-metal host sub-agent (server role).
#
# Publishes src/OpenMono.HostBridge (Release) to $HOME/.openmono/bin/host-bridge
# and writes a sample ~/.openmono/host-bridge.json when none exists.
#
# .NET 10 is required. When it is missing, this script reuses the EXISTING
# prerequisite path (scripts/install_prereqs.sh — the same installer that
# provides .NET 10 for the main agent) instead of inventing a second one.
# That path also ensures Docker, git, curl, and jq, which the server role
# needs for its in-container agent.
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
CSPROJ="$REPO_DIR/src/OpenMono.HostBridge/OpenMono.HostBridge.csproj"
BIN_DIR="${OPENMONO_HOST_BRIDGE_BIN_DIR:-$HOME/.openmono/bin/host-bridge}"

dotnet10_present() {
    command -v dotnet &>/dev/null || return 1
    local major
    major="$(dotnet --version 2>/dev/null | cut -d. -f1 || echo 0)"
    [[ "$major" =~ ^[0-9]+$ && "$major" -ge 10 ]]
}

if ! dotnet10_present; then
    echo "[host-bridge] .NET 10 SDK not found — running the existing prerequisites installer..."
    bash "$SCRIPT_DIR/install_prereqs.sh" \
        || { echo "ERROR: prerequisites installer failed." >&2; exit 1; }
    # prereqs installs the SDK under ~/.dotnet and updates rc files, neither of
    # which affects this shell — pick it up explicitly.
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
        export PATH="$DOTNET_ROOT:$PATH"
    fi
fi

dotnet10_present || {
    echo "ERROR: .NET 10 SDK still not available after prerequisites." >&2
    echo "Install it via scripts/install_prereqs.sh, then retry." >&2
    exit 1
}

if [[ ! -f "$CSPROJ" ]]; then
    echo "ERROR: not found: $CSPROJ (run from the OpenMono.ai checkout)." >&2
    exit 1
fi

echo "[host-bridge] Publishing OpenMono.HostBridge (Release) to $BIN_DIR ..."
mkdir -p "$BIN_DIR"
dotnet publish "$CSPROJ" -c Release -o "$BIN_DIR" \
    || { echo "ERROR: dotnet publish failed." >&2; exit 1; }

if [[ ! -x "$BIN_DIR/host-bridge" ]]; then
    echo "ERROR: publish succeeded but $BIN_DIR/host-bridge is missing." >&2
    exit 1
fi

"$BIN_DIR/host-bridge" --version

# Sudo opt-in (never silent): configure run-as identity + sudo choice now when
# interactive; otherwise leave allow_sudo unasked so the bridge asks once on
# first run. OPENMONO_HOST_SUDO=1/0 presets it for non-interactive installs.
case "${OPENMONO_HOST_SUDO:-}" in
    1|[Yy]|[Yy]es)
        "$BIN_DIR/host-bridge" --init --allow-sudo --non-interactive
        ;;
    0|[Nn]|[Nn]o)
        "$BIN_DIR/host-bridge" --init --no-sudo --non-interactive
        ;;
    *)
        if [[ -t 0 ]]; then
            "$BIN_DIR/host-bridge" --init \
                || echo "[host-bridge] identity setup skipped — first run will ask." >&2
        else
            echo "[host-bridge] non-interactive: leaving sudo unasked (first run will ask)."
        fi
        ;;
esac

echo "[host-bridge] OK — run it with: openmono agent --host"
