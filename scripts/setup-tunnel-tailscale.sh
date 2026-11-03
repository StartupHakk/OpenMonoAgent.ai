#!/usr/bin/env bash
set -euo pipefail

# ─────────────────────────────────────────────────────────────────────
# OpenMono.ai — Set up Tailscale dual-box connectivity on the inference box.
# Joins (or reuses) a Tailscale tailnet so the agent box can reach
# llama-server via MagicDNS or a 100.x address — no frp relay required.
#
# Usage: openmono tunnel setup --tailscale [--auth-key tskey-auth-...]
# ─────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
ENV_FILE="$REPO_DIR/docker/.env"
TS_CACHE="$HOME/.openmono/tailscale-tunnel.json"
SETTINGS_FILE="$HOME/.openmono/settings.json"
LLAMA_PORT="${LLAMA_PORT:-}"

# shellcheck source=lib/tailscale-cli.sh
source "$SCRIPT_DIR/lib/tailscale-cli.sh"

# Prefer explicit LLAMA_PORT; else settings.json inference.port; else 7474
if [[ -z "$LLAMA_PORT" && -f "$SETTINGS_FILE" ]] && command -v jq &>/dev/null; then
    LLAMA_PORT="$(jq -r '.inference.port // empty' "$SETTINGS_FILE" 2>/dev/null || true)"
fi
LLAMA_PORT="${LLAMA_PORT:-7474}"

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
YELLOW=$'\033[1;33m'
BLUE=$'\033[38;2;163;255;102m'
BOLD=$'\033[1m'
NC=$'\033[0m'

info()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

AUTH_KEY="${TS_AUTHKEY:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --auth-key)
            AUTH_KEY="${2:-}"
            if [[ -z "$AUTH_KEY" ]]; then
                err "--auth-key requires a value (tskey-auth-...)"
                exit 1
            fi
            shift 2
            ;;
        --auth-key=*)
            AUTH_KEY="${1#*=}"
            shift
            ;;
        --tailscale)
            # Accepted for convenience when forwarded from openmono
            shift
            ;;
        -h|--help)
            cat <<EOF
Usage: openmono tunnel setup --tailscale [--auth-key tskey-auth-...]

Joins this machine to your Tailscale tailnet (or reuses an existing
session), ensures LLAMA_API_KEY is set, and prints agent-box config.

Auth key (tskey-auth-...) can also be supplied via TS_AUTHKEY.
Create keys at: https://login.tailscale.com/admin/settings/keys

Note: openmono tunnel start/stop/logs remain frp-only. Tailscale runs
as its own daemon (tailscaled).
EOF
            exit 0
            ;;
        *)
            err "Unknown argument: $1"
            err "Usage: openmono tunnel setup --tailscale [--auth-key tskey-auth-...]"
            exit 1
            ;;
    esac
done

# ── Prerequisites ────────────────────────────────────────────────────

if ! command -v openssl &>/dev/null; then
    err "Missing required command: openssl"
    exit 1
fi

if ! command -v jq &>/dev/null; then
    err "Missing required command: jq"
    err "Install: brew install jq   (macOS)  or  apt install jq  (Linux)"
    exit 1
fi

if ! resolve_tailscale_cli; then
    err "tailscale CLI not found."
    echo ""
    case "$(uname -s)" in
        Darwin)
            echo "  Install on macOS:"
            echo "    brew install --cask tailscale"
            echo "    # or open Tailscale.app: https://tailscale.com/download/mac"
            echo "  GUI installs often live at:"
            echo "    /Applications/Tailscale.app/Contents/MacOS/Tailscale"
            ;;
        Linux)
            echo "  Install on Linux:"
            echo "    curl -fsSL https://tailscale.com/install.sh | sh"
            echo "    # or: https://tailscale.com/download/linux"
            ;;
        *)
            echo "  Download: https://tailscale.com/download"
            ;;
    esac
    echo ""
    echo "  After installing, re-run: openmono tunnel setup --tailscale"
    exit 1
fi
info "Using Tailscale CLI: $TAILSCALE_BIN"

# ── Join or reuse Tailscale session ──────────────────────────────────

ts_backend_up() {
    tailscale_is_running
}

# Surface common macOS failure modes early (logged out / prefs).
_pre_backend="$(tailscale_backend_state 2>/dev/null || true)"
if [[ "$_pre_backend" == "NeedsLogin" || "$_pre_backend" == "PrefsError" ]]; then
    warn "Tailscale state: $_pre_backend"
    tailscale_login_hint || true
    # Fall through to auth-key / browser login below
fi

if ts_backend_up; then
    ok "Already connected to Tailscale"
else
    info "Not connected to a Tailscale tailnet yet"

    if [[ -z "$AUTH_KEY" ]]; then
        if [[ -t 0 ]]; then
            echo ""
            echo "Create an auth key (tskey-auth-...) at:"
            echo "  https://login.tailscale.com/admin/settings/keys"
            echo ""
            echo "Recommended for always-on inference boxes: non-ephemeral;"
            echo "enable Pre-approved if your tailnet requires device approval."
            echo ""
            echo "Leave blank to open a browser login instead."
            echo ""
            # Prefer printing any live login URL from `tailscale status`
            login_url="$(tailscale_cmd_capture status 2>/dev/null | grep -Eo 'https://login\.tailscale\.com/[^[:space:]]+' | head -1 || true)"
            [[ -n "$login_url" ]] && echo "Browser login URL: $login_url"
            echo ""
            read -r -p "Auth key (or Enter for browser login): " AUTH_KEY
            AUTH_KEY="$(echo "$AUTH_KEY" | tr -d '[:space:]')"
        fi
    fi

    if [[ -n "$AUTH_KEY" ]]; then
        if [[ "$AUTH_KEY" == tskey-api-* ]]; then
            err "That looks like an API access token (tskey-api-...), not an auth key."
            err "Use a device auth key (tskey-auth-...) for joining this machine."
            err "Create one at: https://login.tailscale.com/admin/settings/keys"
            exit 1
        fi
        info "Joining Tailscale with auth key..."
        if ! sudo "$TAILSCALE_BIN" up --auth-key="$AUTH_KEY" 2>/dev/null && \
           ! tailscale_cmd up --auth-key="$AUTH_KEY"; then
            err "tailscale up --auth-key failed"
            tailscale_login_hint || true
            exit 1
        fi
    else
        info "Opening interactive Tailscale login (browser)..."
        # Prefer `login` on macOS app builds when available
        if ! tailscale_cmd login 2>/dev/null && \
           ! sudo "$TAILSCALE_BIN" up 2>/dev/null && \
           ! tailscale_cmd up; then
            err "tailscale login/up failed"
            tailscale_login_hint || true
            exit 1
        fi
    fi

    # Wait briefly for BackendState=Running
    local_wait=0
    while (( local_wait < 45 )); do
        if ts_backend_up; then
            break
        fi
        sleep 1
        local_wait=$((local_wait + 1))
    done

    if ! ts_backend_up; then
        err "Tailscale did not reach Running state. Check: tailscale status"
        tailscale_login_hint || true
        exit 1
    fi
    ok "Connected to Tailscale"
fi

# ── Resolve MagicDNS + IPv4 ──────────────────────────────────────────

TS_STATUS_JSON="$(tailscale_cmd status --json)"
TS_DNS="$(echo "$TS_STATUS_JSON" | jq -r '.Self.DNSName // empty' | sed 's/\.$//')"
TS_IP="$(tailscale_cmd ip -4 2>/dev/null | head -1 || true)"
if [[ -z "$TS_IP" ]]; then
    TS_IP="$(echo "$TS_STATUS_JSON" | jq -r '.Self.TailscaleIPs[]? // empty' | grep -E '^[0-9]+\.' | head -1 || true)"
fi

if [[ -z "$TS_IP" && -z "$TS_DNS" ]]; then
    err "Could not resolve Tailscale IP or MagicDNS name"
    err "Check: tailscale status"
    exit 1
fi

# Prefer MagicDNS hostname when available
if [[ -n "$TS_DNS" ]]; then
    ENDPOINT_HOST="$TS_DNS"
else
    ENDPOINT_HOST="$TS_IP"
fi
ENDPOINT="http://${ENDPOINT_HOST}:${LLAMA_PORT}"

# ── Reuse or generate LLAMA_API_KEY ──────────────────────────────────

LLAMA_API_KEY=""
if [[ -f "$ENV_FILE" ]]; then
    LLAMA_API_KEY="$(grep '^LLAMA_API_KEY=' "$ENV_FILE" | cut -d= -f2- | tr -d '[:space:]' || true)"
fi

# Fall back to native settings.json if docker/.env has no key yet
if [[ -z "$LLAMA_API_KEY" && -f "$SETTINGS_FILE" ]]; then
    LLAMA_API_KEY="$(jq -r '.inference.api_key // .llm.api_key // empty' "$SETTINGS_FILE" 2>/dev/null || true)"
fi

if [[ -z "$LLAMA_API_KEY" ]]; then
    LLAMA_API_KEY="$(openssl rand -hex 24)"
    info "Generated new LLAMA_API_KEY"
else
    info "Reusing existing LLAMA_API_KEY"
fi

mkdir -p "$(dirname "$ENV_FILE")"
touch "$ENV_FILE"
grep -v '^LLAMA_API_KEY=' "$ENV_FILE" > "$ENV_FILE.tmp" || true
echo "LLAMA_API_KEY=$LLAMA_API_KEY" >> "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"
chmod 0600 "$ENV_FILE"
ok "Wrote LLAMA_API_KEY to $ENV_FILE"

# Keep native settings in sync when present (macOS Metal path)
if [[ -f "$SETTINGS_FILE" ]]; then
    tmp_settings="$(mktemp)"
    if jq --arg k "$LLAMA_API_KEY" '
            .inference = ((.inference // {}) | . + {api_key: $k}) |
            if ((.llm.api_key // "") | length) == 0 then
                .llm = ((.llm // {}) | . + {api_key: $k})
            else . end
        ' "$SETTINGS_FILE" > "$tmp_settings" 2>/dev/null; then
        mv "$tmp_settings" "$SETTINGS_FILE"
    else
        rm -f "$tmp_settings"
    fi
fi

# ── Restart llama-server so it picks up the API key ──────────────────

if [[ "$(uname -s)" == "Darwin" && "$(uname -m)" == "arm64" ]] \
    && [[ -f "$REPO_DIR/scripts/macos/inference.sh" ]]; then
    # Native path: caller may restart via openmono; try gentle nudge if running
    if command -v llama-server &>/dev/null; then
        if curl -sf "http://localhost:${LLAMA_PORT}/health" &>/dev/null; then
            info "llama-server is running — restart with: openmono restart"
            info "(needed if the API key just changed)"
        else
            info "llama-server not running yet. Start it with: openmono start"
        fi
    fi
elif command -v docker &>/dev/null && docker compose version &>/dev/null 2>&1; then
    if (cd "$REPO_DIR/docker" && docker compose ps --services 2>/dev/null | grep -q '^llama-server$'); then
        info "Restarting llama-server with API key..."
        (cd "$REPO_DIR/docker" && docker compose up -d llama-server) || \
            warn "Restart failed — run manually: cd ${REPO_DIR}/docker && docker compose up -d llama-server"
    else
        info "llama-server not running yet. Start it with: openmono start"
    fi
else
    warn "Could not auto-restart llama-server. Start/restart with: openmono start"
fi

# ── Cache for tunnel status / rotate-key ─────────────────────────────

mkdir -p "$(dirname "$TS_CACHE")"
jq -n \
    --arg mode "tailscale" \
    --arg hostname "$TS_DNS" \
    --arg ip "$TS_IP" \
    --arg endpoint "$ENDPOINT" \
    --arg port "$LLAMA_PORT" \
    --arg updated "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    '{
        mode: $mode,
        hostname: $hostname,
        ip: $ip,
        endpoint: $endpoint,
        port: ($port | tonumber),
        updatedAt: $updated
    }' > "$TS_CACHE"
chmod 0600 "$TS_CACHE"
ok "Wrote $TS_CACHE"

# Optional local reachability check via Tailscale IP (same host)
if [[ -n "$TS_IP" ]]; then
    if curl -sf --max-time 3 "http://${TS_IP}:${LLAMA_PORT}/health" &>/dev/null; then
        ok "llama-server reachable via Tailscale IP on :${LLAMA_PORT}"
    else
        warn "llama-server not reachable yet at http://${TS_IP}:${LLAMA_PORT}/health"
        warn "Start/restart it with: openmono start   (then re-check from the agent box)"
    fi
fi

# ── Report ───────────────────────────────────────────────────────────

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${GREEN}✓ Tailscale dual-box ready${NC}"
echo -e "${GREEN}✓ LLAMA_API_KEY stored in docker/.env${NC}"
echo -e "${GREEN}✓ Endpoint: $ENDPOINT${NC}"
[[ -n "$TS_IP" ]] && echo -e "${GREEN}✓ Tailscale IPv4: $TS_IP${NC}"
[[ -n "$TS_DNS" ]] && echo -e "${GREEN}✓ MagicDNS: $TS_DNS${NC}"
echo ""
echo -e "${BLUE}ON THE AGENT BOX${NC} (same tailnet), run:"
echo ""
echo "  openmono config set llm.endpoint  $ENDPOINT"
echo "  openmono config set llm.api_key   $LLAMA_API_KEY"
echo ""
echo "Then:  openmono agent"
echo ""
echo -e "${YELLOW}Notes:${NC}"
echo "  • Agent box must also run Tailscale and join the same tailnet."
echo "  • openmono tunnel start/stop/logs are frp-only; Tailscale is its own daemon."
echo "  • Check connectivity:  openmono tunnel status"
echo "  • Auth keys vs API tokens: https://login.tailscale.com/admin/settings/keys"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""