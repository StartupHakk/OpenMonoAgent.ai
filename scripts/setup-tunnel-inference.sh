#!/usr/bin/env bash
set -euo pipefail

# ─────────────────────────────────────────────────────────────────────
# OpenMono.ai — Set up frp client on the inference box.
# Connects outbound to an OpenMonoAgent Relay instance so the agent box
# can reach this machine's llama-server without port forwarding.
#
# Usage: openmono tunnel setup
# ─────────────────────────────────────────────────────────────────────

FRP_VERSION="${FRP_VERSION:-0.61.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
ENV_FILE="$REPO_DIR/docker/.env"
RELAY_CACHE="$HOME/.openmono/relay.json"
API_BASE="https://app.openmonoagent.ai"
RELAY_PUBLIC_HOST="relay.openmonoagent.ai"

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
YELLOW=$'\033[1;33m'
BLUE=$'\033[38;2;163;255;102m'
NC=$'\033[0m'

info()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# ── Prerequisite checks ──────────────────────────────────────────────

if [[ $EUID -ne 0 ]] && ! command -v sudo &>/dev/null; then
    err "Must run as root or have sudo installed."
    exit 1
fi

for cmd in curl tar openssl jq systemctl; do
    if ! command -v "$cmd" &>/dev/null; then
        err "Missing required command: $cmd"
        exit 1
    fi
done

# ── Load or obtain relay credentials via OTP ─────────────────────────

FRPS_ADDRESS=""
FRPS_PORT=""
RELAY_TOKEN=""
REMOTE_PORT=""
PROXY_PREFIX=""

if [[ -f "$RELAY_CACHE" ]]; then
    _token="$(jq -r '.relayToken // empty' "$RELAY_CACHE" 2>/dev/null || true)"
    if [[ -n "$_token" ]]; then
        info "Found existing relay credentials for $(jq -r '.email // "unknown"' "$RELAY_CACHE")"
        RELAY_TOKEN="$(jq -r '.relayToken'    "$RELAY_CACHE")"
        REMOTE_PORT="$(jq -r '.remotePort'    "$RELAY_CACHE")"
        PROXY_PREFIX="$(jq -r '.proxyPrefix'  "$RELAY_CACHE")"
        FRPS_ADDRESS="$(jq -r '.frpsAddress'  "$RELAY_CACHE")"
        FRPS_PORT="$(jq -r    '.frpsPort'     "$RELAY_CACHE")"
    fi
fi

if [[ -z "$RELAY_TOKEN" ]]; then
    echo ""
    echo -e "${BLUE}OpenMono.ai${NC} — Relay Tunnel Setup"
    echo "─────────────────────────────────────────"
    echo ""

    # Ask for email
    printf "  Enter your email address: "
    read -r USER_EMAIL
    if [[ -z "$USER_EMAIL" || "$USER_EMAIL" != *@* ]]; then
        err "Invalid email address."
        exit 1
    fi

    # Request OTP
    echo ""
    info "Sending verification code to $USER_EMAIL..."
    _otp_req=$(curl -sf -w "\n%{http_code}" \
        -X POST "$API_BASE/api/cli/otp" \
        -H "Content-Type: application/json" \
        -d "{\"email\":\"$USER_EMAIL\"}" 2>/dev/null || true)

    _otp_code=$(echo "$_otp_req" | tail -1)
    if [[ "$_otp_code" == "429" ]]; then
        err "Too many requests. Try again in a few minutes."
        exit 1
    elif [[ "$_otp_code" != "200" && "$_otp_code" != "201" ]]; then
        err "Failed to send code (HTTP $_otp_code). Check your connection."
        exit 1
    fi

    ok "Code sent to $USER_EMAIL"
    echo ""
    printf "  Enter the code from your email: "
    read -r USER_OTP

    # Verify OTP
    echo ""
    info "Verifying..."
    _verify_resp=$(curl -sf -w "\n%{http_code}" \
        -X POST "$API_BASE/api/cli/otp/verify" \
        -H "Content-Type: application/json" \
        -d "{\"email\":\"$USER_EMAIL\",\"otp\":\"$USER_OTP\"}" 2>/dev/null || true)

    _verify_http=$(echo "$_verify_resp" | tail -1)
    _verify_body=$(echo "$_verify_resp" | head -n -1)

    if [[ "$_verify_http" == "429" ]]; then
        err "Too many incorrect attempts. Run the command again to get a new code."
        exit 1
    elif [[ "$_verify_http" != "200" ]]; then
        err "Invalid or expired code (HTTP $_verify_http). Run the command again to get a new one."
        exit 1
    fi

    RELAY_TOKEN="$(echo "$_verify_body" | jq -r '.relayToken')"
    REMOTE_PORT="$(echo "$_verify_body" | jq -r '.remotePort')"
    PROXY_PREFIX="$(echo "$_verify_body" | jq -r '.proxyPrefix')"
    FRPS_ADDRESS="$(echo "$_verify_body" | jq -r '.frpsAddress')"
    FRPS_PORT="$(echo "$_verify_body"    | jq -r '.frpsPort')"

    if [[ -z "$RELAY_TOKEN" || "$RELAY_TOKEN" == "null" ]]; then
        err "Unexpected response from server. Contact support."
        exit 1
    fi

    # Save credentials
    mkdir -p "$(dirname "$RELAY_CACHE")"
    jq -n \
        --arg email     "$USER_EMAIL" \
        --arg token     "$RELAY_TOKEN" \
        --argjson port  "$REMOTE_PORT" \
        --arg prefix    "$PROXY_PREFIX" \
        --arg addr      "$FRPS_ADDRESS" \
        --argjson fport "$FRPS_PORT" \
        '{email:$email,relayToken:$token,remotePort:$port,proxyPrefix:$prefix,frpsAddress:$addr,frpsPort:$fport}' \
        > "$RELAY_CACHE"
    chmod 0600 "$RELAY_CACHE"
    ok "Credentials saved to $RELAY_CACHE"
fi

# ── Validate ─────────────────────────────────────────────────────────

if [[ -z "$FRPS_ADDRESS" || -z "$RELAY_TOKEN" || -z "$REMOTE_PORT" || -z "$PROXY_PREFIX" ]]; then
    err "Relay credentials incomplete. Delete $RELAY_CACHE and run again."
    exit 1
fi

if ! [[ "$RELAY_TOKEN" =~ ^omr_ ]]; then
    warn "relayToken does not start with 'omr_' — double-check credentials."
fi
if ! [[ "$REMOTE_PORT" =~ ^[0-9]+$ ]]; then
    err "remotePort must be numeric (got: $REMOTE_PORT)"
    exit 1
fi

# ── Generate a fresh API key for llama-server ────────────────────────

LLAMA_API_KEY="$(openssl rand -hex 24)"

# ── Detect architecture ──────────────────────────────────────────────

case "$(uname -m)" in
    aarch64) ARCH="arm64" ;;
    x86_64)  ARCH="amd64" ;;
    *) err "Unsupported architecture: $(uname -m)"; exit 1 ;;
esac

info "Detected arch: linux_$ARCH"

# ── Download and install frpc ────────────────────────────────────────

TMP="$(mktemp -d)"
trap "rm -rf $TMP" EXIT

info "Downloading frp v$FRP_VERSION..."
curl -fL \
    "https://github.com/fatedier/frp/releases/download/v${FRP_VERSION}/frp_${FRP_VERSION}_linux_${ARCH}.tar.gz" \
    -o "$TMP/frp.tar.gz"

tar xz -C "$TMP" --strip-components=1 -f "$TMP/frp.tar.gz"
sudo install -m 0755 "$TMP/frpc" /usr/local/bin/frpc
ok "Installed /usr/local/bin/frpc"

# ── Write frpc.toml ──────────────────────────────────────────────────

sudo mkdir -p /etc/frp
sudo tee /etc/frp/frpc.toml > /dev/null <<EOF
# frp client — OpenMono.ai inference-box side
# Generated by openmono tunnel setup on $(date -u +%Y-%m-%dT%H:%M:%SZ)

serverAddr = "$FRPS_ADDRESS"
serverPort = $FRPS_PORT

metadatas.token = "$RELAY_TOKEN"

transport.tls.enable = true

log.to    = "console"
log.level = "info"

[[proxies]]
name              = "${PROXY_PREFIX}llama"
type              = "tcp"
localIP           = "127.0.0.1"
localPort         = 7474
remotePort        = $REMOTE_PORT
metadatas.token   = "$RELAY_TOKEN"
EOF
sudo chmod 0600 /etc/frp/frpc.toml
ok "Wrote /etc/frp/frpc.toml"

# ── Store LLAMA_API_KEY in docker-compose .env ───────────────────────

mkdir -p "$(dirname "$ENV_FILE")"
touch "$ENV_FILE"

grep -v '^LLAMA_API_KEY=' "$ENV_FILE" > "$ENV_FILE.tmp" || true
echo "LLAMA_API_KEY=$LLAMA_API_KEY" >> "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"
chmod 0600 "$ENV_FILE"
ok "Wrote LLAMA_API_KEY to $ENV_FILE"

# ── systemd unit ─────────────────────────────────────────────────────

sudo tee /etc/systemd/system/frpc.service > /dev/null <<EOF
[Unit]
Description=frp client (OpenMono.ai inference-box side)
After=network.target docker.service
Wants=docker.service

[Service]
Type=simple
ExecStart=/usr/local/bin/frpc -c /etc/frp/frpc.toml
Restart=on-failure
RestartSec=10s

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now frpc
sleep 2

if systemctl is-active --quiet frpc; then
    ok "frpc is running and connected to $FRPS_ADDRESS:$FRPS_PORT"
else
    err "frpc failed to start. Check: sudo journalctl -u frpc"
    err "Common causes: wrong relayToken, revoked token, relay unreachable, firewall blocking outbound :$FRPS_PORT"
    exit 1
fi

# ── Restart llama-server so it picks up the new API key ──────────────

if command -v docker &>/dev/null && docker compose version &>/dev/null 2>&1; then
    if (cd "$REPO_DIR/docker" && docker compose ps --services 2>/dev/null | grep -q '^llama-server$'); then
        info "Restarting llama-server with new API key..."
        (cd "$REPO_DIR/docker" && docker compose restart llama-server) || \
            warn "Restart failed — run manually: cd docker && docker compose restart llama-server"
    else
        info "llama-server not running yet. Start it with: openmono start"
    fi
else
    warn "docker compose not found. Start llama-server manually after installing Docker."
fi

# ── Report ───────────────────────────────────────────────────────────

cat <<EOF

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
${GREEN}✓ frp tunnel connected to $FRPS_ADDRESS:$FRPS_PORT${NC}
${GREEN}✓ LLAMA_API_KEY stored in docker/.env${NC}
${GREEN}✓ Public endpoint: http://$RELAY_PUBLIC_HOST:$REMOTE_PORT${NC}

${BLUE}ON THE AGENT BOX, run:${NC}

  openmono config set llm.endpoint  http://$RELAY_PUBLIC_HOST:$REMOTE_PORT
  openmono config set llm.api_key   $LLAMA_API_KEY

Then:  openmono agent

${YELLOW}To check tunnel status:${NC}  sudo systemctl status frpc
${YELLOW}To tail tunnel logs:${NC}      sudo journalctl -u frpc -f
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
EOF
