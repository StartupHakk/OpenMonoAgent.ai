#!/usr/bin/env bash
set -euo pipefail

# ─────────────────────────────────────────────────────────────────────
# OpenMono.ai — Dual-box prep checklist (macOS)
#
# Run on each machine before/while wiring:
#   Mac Mini (Apple Silicon) → inference
#   Intel iMac / laptop      → agent
#
# Usage:
#   openmono tunnel prepare
#   openmono tunnel prepare --inference
#   openmono tunnel prepare --agent
# ─────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
# shellcheck source=lib/tailscale-cli.sh
source "$SCRIPT_DIR/lib/tailscale-cli.sh"

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
YELLOW=$'\033[1;33m'
BLUE=$'\033[38;2;163;255;102m'
BOLD=$'\033[1m'
NC=$'\033[0m'

info()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[FAIL]${NC} $*"; }

ROLE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --inference) ROLE=inference; shift ;;
        --agent)     ROLE=agent; shift ;;
        -h|--help)
            cat <<EOF
Usage: openmono tunnel prepare [--inference|--agent]

Checks this Mac for dual-box readiness and prints next commands.
Wi-Fi + Tailscale is enough — no Ethernet between boxes required.
EOF
            exit 0
            ;;
        *)
            err "Unknown arg: $1"
            exit 1
            ;;
    esac
done

OS="$(uname -s)"
ARCH="$(uname -m)"

if [[ -z "$ROLE" ]]; then
    if [[ "$OS" == "Darwin" && "$ARCH" == "arm64" ]]; then
        ROLE=inference
        info "Auto-role: inference (Apple Silicon detected)"
    else
        ROLE=agent
        info "Auto-role: agent ($( [[ "$OS" == "Darwin" ]] && echo "Intel/non-arm Mac" || echo "$OS/$ARCH" ))"
    fi
fi

echo ""
echo -e "${BOLD}OpenMono dual-box prep — role: ${ROLE}${NC}"
echo "  Machine: $OS $ARCH"
echo "  Repo:    $REPO_DIR"
echo "  Link:    Wi-Fi + Tailscale (same tailnet)"
echo ""

PASS=0
FAIL=0
WARN=0

check_pass() { ok "$1"; PASS=$((PASS + 1)); }
check_fail() { err "$1"; FAIL=$((FAIL + 1)); }
check_warn() { warn "$1"; WARN=$((WARN + 1)); }

# ── Shared checks ────────────────────────────────────────────────────

if [[ "$OS" != "Darwin" ]]; then
    check_warn "This prep script targets macOS; continuing anyway"
fi

if resolve_tailscale_cli; then
    check_pass "Tailscale CLI: $TAILSCALE_BIN"
    backend="$(tailscale_backend_state 2>/dev/null || true)"
    if [[ "$backend" == "Running" ]]; then
        ts_json="$(tailscale_cmd status --json 2>/dev/null || true)"
        dns="$(echo "$ts_json" | jq -r '.Self.DNSName // empty' 2>/dev/null | sed 's/\.$//' || true)"
        ip="$(tailscale_cmd ip -4 2>/dev/null | head -1 || true)"
        check_pass "Tailscale Running${dns:+ — $dns}${ip:+ ($ip)}"
    else
        check_fail "Tailscale not Running (state: ${backend:-unknown}). Open Tailscale.app and sign in."
        tailscale_login_hint 2>&1 | sed 's/^/         /' || true
    fi
else
    check_fail "Tailscale CLI not found"
    echo "         Install: brew install --cask tailscale"
    echo "         Or open Tailscale.app (GUI) then re-run this check"
fi

if command -v brew &>/dev/null; then
    check_pass "Homebrew: $(command -v brew)"
else
    check_fail "Homebrew missing (needed for OpenMono macOS setup)"
    echo "         /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\""
fi

if command -v jq &>/dev/null; then
    check_pass "jq available"
else
    check_warn "jq missing — install after Homebrew: brew install jq"
fi

# ── Role-specific ────────────────────────────────────────────────────

echo ""
if [[ "$ROLE" == "inference" ]]; then
    echo -e "${BOLD}── Inference box checks (Mac Mini) ──${NC}"

    if [[ "$ARCH" != "arm64" ]]; then
        check_fail "Apple Silicon (arm64) required for Metal inference — this machine is $ARCH"
    else
        check_pass "Apple Silicon OK for Metal llama-server"
    fi

    if command -v llama-server &>/dev/null; then
        check_pass "llama-server: $(command -v llama-server)"
    else
        check_warn "llama-server not installed yet — run: openmono setup --inference"
    fi

    port=7474
    if [[ -f "$HOME/.openmono/settings.json" ]] && command -v jq &>/dev/null; then
        port="$(jq -r '.inference.port // 7474' "$HOME/.openmono/settings.json" 2>/dev/null || echo 7474)"
    fi
    if curl -sf --max-time 2 "http://127.0.0.1:${port}/health" &>/dev/null; then
        check_pass "llama-server healthy on :${port}"
    else
        check_warn "llama-server not healthy on :${port} yet (ok before first setup)"
    fi

    if [[ -f "$HOME/.openmono/tailscale-tunnel.json" ]]; then
        check_pass "Tailscale tunnel cache present"
        if command -v jq &>/dev/null; then
            ep="$(jq -r '.endpoint // empty' "$HOME/.openmono/tailscale-tunnel.json")"
            [[ -n "$ep" ]] && info "Cached agent endpoint: $ep"
        fi
    else
        check_warn "No Tailscale tunnel cache — after setup run: openmono tunnel setup --tailscale"
    fi

    echo ""
    echo -e "${BOLD}Next commands on this Mini${NC}"
    echo "  1. cd $REPO_DIR   # or install via get-openmono.sh"
    echo "  2. ./openmono setup --inference"
    echo "  3. ./openmono tunnel setup --tailscale"
    echo "  4. ./openmono status"
    echo "  5. Copy the printed llm.endpoint + llm.api_key → iMac"
else
    echo -e "${BOLD}── Agent box checks (this Mac) ──${NC}"

    if command -v docker &>/dev/null && docker info &>/dev/null 2>&1; then
        check_pass "Docker daemon reachable"
    elif [[ -d /Applications/Docker.app ]]; then
        check_warn "Docker.app installed but daemon not running — open Docker Desktop"
    elif command -v colima &>/dev/null; then
        check_warn "Colima present but docker not ready — try: colima start"
    else
        check_fail "Docker/Colima missing — run agent setup after Homebrew"
    fi

    endpoint=""
    if [[ -f "$HOME/.openmono/settings.json" ]]; then
        if command -v jq &>/dev/null; then
            endpoint="$(jq -r '.llm.endpoint // empty' "$HOME/.openmono/settings.json")"
            key="$(jq -r '.llm.api_key // empty' "$HOME/.openmono/settings.json")"
            if [[ -n "$endpoint" && "$endpoint" != "http://localhost:7474" ]]; then
                check_pass "llm.endpoint = $endpoint"
            else
                check_warn "llm.endpoint still local/default — set to Mini MagicDNS after Mini setup"
            fi
            key_lc="$(printf '%s' "$key" | tr '[:upper:]' '[:lower:]')"
            if [[ -z "$key" ]] || [[ "$key_lc" == *"paste"* ]] || [[ "$key_lc" == *"changeme"* ]]; then
                if [[ -n "$endpoint" && "$endpoint" != "http://localhost:7474" ]]; then
                    check_fail "llm.api_key empty/placeholder while remote endpoint is set (insecure)"
                    echo "         Fix: ./scripts/apply-mini-endpoint.sh <key-from-mini>"
                    echo "         See: docs/SECURITY.md"
                else
                    check_warn "llm.api_key empty — paste key from Mini"
                fi
            else
                check_pass "llm.api_key is set (${#key} chars)"
            fi
        else
            check_warn "~/.openmono/settings.json exists (install jq to validate endpoint/key)"
        fi
    else
        check_warn "No ~/.openmono/settings.json yet — finish agent setup first"
    fi

    echo ""
    echo -e "${BOLD}Next commands on this agent Mac${NC}"
    echo "  1. If llm.api_key is missing:"
    echo "       ./scripts/secure-mini-ssh-and-key.sh"
    echo "       ./scripts/apply-mini-endpoint.sh <KEY>"
    echo "  2. Verify:  openmono tunnel doctor --agent"
    echo "  3. Run:     cd your-project && openmono agent"
    echo "  4. Push:    ./scripts/github-push.sh   # after GitHub SSH key is added"
fi

echo ""
echo -e "${BOLD}Summary${NC}: ${GREEN}${PASS} ok${NC}, ${YELLOW}${WARN} warn${NC}, ${RED}${FAIL} fail${NC}"
echo ""

if [[ "$FAIL" -gt 0 ]]; then
    exit 1
fi
exit 0
