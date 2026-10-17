#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — install the bare-metal host sub-agent (server role).
#
# Publishes src/OpenMono.HostBridge (Release) to $HOME/.openmono/bin/host-bridge
# and writes a sample ~/.openmono/host-bridge.json when none exists.
#
# The bridge is ON-DEMAND: installing it never starts or runs it. This script
# only publishes the binary and writes the sample config. The run-as identity
# and sudo policy are chosen at install time by scripts/install.sh (which
# prompts and exports OPENMONO_HOST_SUDO); the run-as user is confirmed on the
# first real `openmono agent --host` run if it was left blank. Everything
# below is therefore strictly non-interactive — a prompt here would look like
# a hang when this script is run from `openmono setup`, which redirects stderr
# to the install log.
#
# .NET 10 is required. When it is missing, this script reuses the EXISTING
# prerequisite path (scripts/install_prereqs.sh on Linux, or
# scripts/install_prereqs_macos.sh with OPENMONO_ROLE=agent on macOS — the
# same installers that provide .NET 10 for the main agent) instead of
# inventing a second one. That path also ensures Docker, git, curl, and jq,
# which the server role needs for its in-container agent.
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

# A .NET installed under ~/.dotnet (our prereqs path) is only on PATH via rc
# files, which this non-interactive shell never sourced — pick it up first so
# an already-installed SDK is found without re-running prerequisites.
if ! dotnet10_present && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
    export PATH="$DOTNET_ROOT:$PATH"
fi

if ! dotnet10_present; then
    echo "[host-bridge] .NET 10 SDK not found — running the existing prerequisites installer..."
    if [[ "$(uname -s)" == "Darwin" ]]; then
        # macOS: the Ubuntu/apt prereqs path cannot run here. The macOS
        # prereqs installer covers the server role with OPENMONO_ROLE=agent
        # (Homebrew, Xcode CLT, core tools, Docker, .NET — no llama.cpp).
        OPENMONO_ROLE="${OPENMONO_ROLE:-agent}" bash "$SCRIPT_DIR/install_prereqs_macos.sh" \
            || { echo "ERROR: prerequisites installer failed." >&2; exit 1; }
    else
        bash "$SCRIPT_DIR/install_prereqs.sh" \
            || { echo "ERROR: prerequisites installer failed." >&2; exit 1; }
    fi
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

# Sudo policy preset (never interactive at install time): OPENMONO_HOST_SUDO
# writes allow_sudo into the config; install.sh prompts for it and exports this
# (1/yes→--allow-sudo, 0/no→--no-sudo). When unset the config keeps its default
# ("ask") and the bridge confirms the run-as user on the first real
# `openmono agent --host` run.
# Host-exec policy (never silent): OPENMONO_HOST_EXEC_DEFAULT=allow|ask selects
# the default. Anything but an explicit "allow" stays "ask" (fail closed).
# install.sh prompts for this; direct script runs default to ask.
# Re-runs preserve an existing config: host_exec.default (and everything else)
# is only written for a fresh config, unless the user explicitly set
# OPENMONO_HOST_EXEC_DEFAULT to change it.
_HOST_BRIDGE_CONFIG="${OPENMONO_HOST_BRIDGE_CONFIG:-$HOME/.openmono/host-bridge.json}"
_HOST_BRIDGE_PREEXISTED=0
[[ -f "$_HOST_BRIDGE_CONFIG" ]] && _HOST_BRIDGE_PREEXISTED=1
case "${OPENMONO_HOST_SUDO:-}" in
    1|[Yy]|[Yy]es)
        # Always --non-interactive: --init must never prompt here.
        "$BIN_DIR/host-bridge" --init --allow-sudo --non-interactive 2>&1
        ;;
    0|[Nn]|[Nn]o)
        "$BIN_DIR/host-bridge" --init --no-sudo --non-interactive 2>&1
        ;;
    *)
        # Ensure the config exists even on the default path: a flag-less
        # --non-interactive --init only writes the sample when missing and
        # preserves every existing value — so the host-exec default block
        # below always has a file to update (a fresh exec=allow answer would
        # otherwise be silently dropped).
        "$BIN_DIR/host-bridge" --init --non-interactive 2>&1
        echo "[host-bridge] sudo policy left as default (ask) — each escalation is confirmed [y/N] at run time."
        ;;
esac

echo "[host-bridge] OK — run it with: openmono agent --host (first run confirms the run-as user if left blank; change later with: openmono config set host.run_as <user> / host.sudo_policy ask|allow|deny)"

# Apply the host-exec default. Fresh configs get a default (ask unless
# OPENMONO_HOST_EXEC_DEFAULT=allow). Existing configs are preserved as-is
# unless the user explicitly set OPENMONO_HOST_EXEC_DEFAULT to change them.
_HOST_EXEC_DEFAULT="ask"
_HOST_EXEC_EXPLICIT=0
case "${OPENMONO_HOST_EXEC_DEFAULT:-}" in
    [Aa]llow) _HOST_EXEC_DEFAULT="allow"; _HOST_EXEC_EXPLICIT=1 ;;
    [Aa]sk) _HOST_EXEC_DEFAULT="ask"; _HOST_EXEC_EXPLICIT=1 ;;
esac
if [[ "$_HOST_BRIDGE_PREEXISTED" == "1" && "$_HOST_EXEC_EXPLICIT" != "1" ]]; then
    echo "[host-bridge] preserving existing config $_HOST_BRIDGE_CONFIG (set OPENMONO_HOST_EXEC_DEFAULT=allow|ask to change host_exec.default)."
elif [[ -f "$_HOST_BRIDGE_CONFIG" ]] && command -v python3 &>/dev/null; then
    _HOST_EXEC_DEFAULT="$_HOST_EXEC_DEFAULT" _HOST_BRIDGE_CONFIG="$_HOST_BRIDGE_CONFIG" python3 - <<'PYEOF' || echo "[host-bridge] WARNING: could not set host_exec.default; edit $_HOST_BRIDGE_CONFIG manually." >&2
import json, os
cfg_path = os.environ["_HOST_BRIDGE_CONFIG"]
want = os.environ["_HOST_EXEC_DEFAULT"]
try:
    with open(cfg_path) as f:
        cfg = json.load(f)
    cfg.setdefault("host_exec", {})["default"] = want
    with open(cfg_path, "w") as f:
        json.dump(cfg, f, indent=2)
        f.write("\n")
    print(f"[host-bridge] host-exec default: {want} ({cfg_path})")
except Exception as ex:
    print(f"[host-bridge] WARNING: {ex}")
    raise SystemExit(1)
PYEOF
else
    echo "[host-bridge] host-exec default: $_HOST_EXEC_DEFAULT (ask = confirm each command; allow = run routine without asking)"
fi
