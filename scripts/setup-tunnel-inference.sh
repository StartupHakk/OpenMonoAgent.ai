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

# If the Caddy web gateway is installed, tunnel it instead of llama directly —
# the single remote port then reaches llama + search + scrape via path routing.
GATEWAY_PORT="$(grep '^GATEWAY_PORT=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- | tr -d '[:space:]' || true)"
GATEWAY_PORT="${GATEWAY_PORT:-47480}"
# Tunnel the gateway (which fronts llama + any web services) whenever it's
# installed; otherwise fall back to tunneling llama-server directly.
if grep -q '^GATEWAY_ENABLED=true' "$ENV_FILE" 2>/dev/null || grep -qE '^WEB_(SEARCH|SCRAPE)_ENABLED=true' "$ENV_FILE" 2>/dev/null; then
    TUNNEL_LOCAL_PORT="$GATEWAY_PORT"
else
    TUNNEL_LOCAL_PORT=7474
fi
# The agent box probes the gateway's /services registry (same relay URL as
# llm.endpoint) and routes WebSearch/WebFetch through whatever this box exposes,
# so the agent-box instructions only ever need llm.endpoint + llm.api_key.

_SETUP_OS=$(uname -s)
_SETUP_ARCH=$(uname -m)
NATIVE_INFERENCE=false
[[ "$_SETUP_OS" == "Darwin" && "$_SETUP_ARCH" == "arm64" ]] && NATIVE_INFERENCE=true

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
YELLOW=$'\033[1;33m'
BLUE=$'\033[38;2;163;255;102m'
NC=$'\033[0m'

info()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# ── Detect OS ───────────────────────────────────────────────────────

OS_TYPE="$(uname -s)"
case "$OS_TYPE" in
    Darwin) OS="darwin" ;;
    Linux)  OS="linux" ;;
    *) err "Unsupported OS: $OS_TYPE"; exit 1 ;;
esac

# ── Prerequisite checks ──────────────────────────────────────────────

if [[ $EUID -ne 0 ]] && ! command -v sudo &>/dev/null; then
    err "Must run as root or have sudo installed."
    exit 1
fi

required_cmds="curl tar openssl jq"
[[ "$OS" == "linux" ]] && required_cmds="$required_cmds systemctl"

for cmd in $required_cmds; do
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
LLAMA_API_KEY=""

if [[ -f "$RELAY_CACHE" ]]; then
    # Show current configuration and ask if user wants to reuse it
    _token="$(jq -r '.relayToken // empty' "$RELAY_CACHE" 2>/dev/null || true)"
    if [[ -z "$_token" ]]; then
        err "Relay cache exists but has no relayToken. Delete $RELAY_CACHE and run setup again."
        exit 1
    fi
    if [[ -n "$_token" ]]; then
        info "Found existing relay credentials for $(jq -r '.email // "unknown"' "$RELAY_CACHE")"
        RELAY_TOKEN="$(jq -r '.relayToken'    "$RELAY_CACHE")"
        REMOTE_PORT="$(jq -r '.remotePort'    "$RELAY_CACHE")"
        PROXY_PREFIX="$(jq -r '.proxyPrefix'  "$RELAY_CACHE")"
        FRPS_ADDRESS="$(jq -r '.frpsAddress'  "$RELAY_CACHE")"
        FRPS_PORT="$(jq -r    '.frpsPort'     "$RELAY_CACHE")"
    fi
    
    if [[ -f "$ENV_FILE" ]]; then
        LLAMA_API_KEY="$(grep '^LLAMA_API_KEY=' "$ENV_FILE" | cut -d= -f2- | tr -d '[:space:]' || true)"
    fi

    if [[ -z "$LLAMA_API_KEY" ]]; then
        err "No LLAMA_API_KEY found in $ENV_FILE"
        err "Run 'openmono tunnel setup'"
        exit 1
    fi

    cat <<EOF

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
${GREEN}Inference box connection details${NC}

  LLAMA API Key:  $LLAMA_API_KEY
  Base URL:       http://$RELAY_PUBLIC_HOST:$REMOTE_PORT

${BLUE}ON THE AGENT BOX, run:${NC}

  openmono config set llm.endpoint  http://$RELAY_PUBLIC_HOST:$REMOTE_PORT
  openmono config set llm.api_key   $LLAMA_API_KEY

Then:  openmono agent

${YELLOW}Relay server:${NC} $FRPS_ADDRESS:$FRPS_PORT
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
EOF

    info "Sending connection details to your registered email..."
    CONNECT_HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
        -X POST "$API_BASE/api/connection/connect" \
        -H "Authorization: Bearer $RELAY_TOKEN" \
        -H "Content-Type: application/json" \
        -d "{\"apiKey\": \"$LLAMA_API_KEY\"}")

    if [[ "$CONNECT_HTTP_CODE" == "200" ]]; then
        ok "Connection details sent to your email."
    else
        warn "Could not send connection email (HTTP $CONNECT_HTTP_CODE)."
    fi

    exit 0
    else
    # No valid cached credentials, go through OTP flow
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
    _verify_body=$(echo "$_verify_resp" | sed '$d')

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

# ── Reuse existing API key, or generate one if absent ────────────────

if [[ -f "$ENV_FILE" ]]; then
    LLAMA_API_KEY="$(grep '^LLAMA_API_KEY=' "$ENV_FILE" | cut -d= -f2- | tr -d '[:space:]' || true)"
fi
if [[ -z "$LLAMA_API_KEY" ]]; then
    LLAMA_API_KEY="$(openssl rand -hex 24)"
    info "Generated new LLAMA_API_KEY"
else
    info "Reusing existing LLAMA_API_KEY from $ENV_FILE"
fi

# ── Detect architecture ──────────────────────────────────────────────

case "$(uname -m)" in
    aarch64) ARCH="arm64" ;;
    arm64)   ARCH="arm64" ;;
    x86_64)  ARCH="amd64" ;;
    *) err "Unsupported architecture: $(uname -m)"; exit 1 ;;
esac

info "Detected arch: ${OS}_$ARCH"

# ── Install frpc via Homebrew or download binary ──────────────────────

if [[ "$OS" == "darwin" ]]; then
    if ! command -v brew &>/dev/null; then
        err "Homebrew not installed. Please install Homebrew first: https://brew.sh"
        exit 1
    fi

    info "Installing frpc via Homebrew..."
    if ! brew install frpc >/dev/null 2>&1; then
        err "Failed to install frpc via Homebrew"
        exit 1
    fi
    ok "Installed frpc via Homebrew"
else
    # Linux: download binary
    TMP="$(mktemp -d)"
    trap "rm -rf $TMP" EXIT

    info "Downloading frp v$FRP_VERSION for ${OS}_${ARCH}..."
    curl -fL \
        "https://github.com/fatedier/frp/releases/download/v${FRP_VERSION}/frp_${FRP_VERSION}_${OS}_${ARCH}.tar.gz" \
        -o "$TMP/frp.tar.gz"

    if ! tar xz -C "$TMP" -f "$TMP/frp.tar.gz"; then
        err "Failed to extract frp archive"
        exit 1
    fi

    if [[ ! -f "$TMP/frp_${FRP_VERSION}_${OS}_${ARCH}/frpc" ]]; then
        err "frpc binary not found in extracted archive"
        exit 1
    fi

    info "Installing frpc to /usr/local/bin/frpc"
    sudo mkdir -p /usr/local/bin
    sudo cp "$TMP/frp_${FRP_VERSION}_${OS}_${ARCH}/frpc" /usr/local/bin/frpc
    sudo chmod 0755 /usr/local/bin/frpc
    ok "Installed /usr/local/bin/frpc"
fi

# ── Write frpc.toml ──────────────────────────────────────────────────

FRP_CONFIG_CONTENT=$(cat <<EOF
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
localPort         = $TUNNEL_LOCAL_PORT
remotePort        = $REMOTE_PORT
metadatas.token   = "$RELAY_TOKEN"
EOF
)

if [[ "$OS" == "darwin" ]]; then
    FRP_CONFIG_DIR="$HOME/.config/frp"
    mkdir -p "$FRP_CONFIG_DIR"
    echo "$FRP_CONFIG_CONTENT" | tee "$FRP_CONFIG_DIR/frpc.toml" > /dev/null
    chmod 0600 "$FRP_CONFIG_DIR/frpc.toml"
    ok "Wrote $FRP_CONFIG_DIR/frpc.toml"
else
    FRP_CONFIG_DIR="/etc/frp"
    sudo mkdir -p "$FRP_CONFIG_DIR"
    echo "$FRP_CONFIG_CONTENT" | sudo tee "$FRP_CONFIG_DIR/frpc.toml" > /dev/null
    sudo chmod 0600 "$FRP_CONFIG_DIR/frpc.toml"
    ok "Wrote $FRP_CONFIG_DIR/frpc.toml"
fi

# ── Store LLAMA_API_KEY in docker-compose .env ───────────────────────

mkdir -p "$(dirname "$ENV_FILE")"
touch "$ENV_FILE"

grep -v '^LLAMA_API_KEY=' "$ENV_FILE" > "$ENV_FILE.tmp" || true
echo "LLAMA_API_KEY=$LLAMA_API_KEY" >> "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"
chmod 0600 "$ENV_FILE"
ok "Wrote LLAMA_API_KEY to $ENV_FILE"

# ── Start frpc service ──────────────────────────────────────────────

if [[ "$OS" == "darwin" ]]; then
    # macOS: Test config first
    info "Testing frpc configuration..."
    if ! timeout 5 frpc -c "$FRP_CONFIG_DIR/frpc.toml" 2>&1 | grep -q "start proxy"; then
        warn "Config test timed out (expected for long-running service), continuing..."
    fi

    # Copy config to Homebrew's expected location for background service
    BREW_PREFIX=$(brew --prefix)
    BREW_FRP_CONFIG="$BREW_PREFIX/etc/frp"
    info "Installing config to $BREW_FRP_CONFIG/frpc.toml for Homebrew service..."
    mkdir -p "$BREW_FRP_CONFIG"
    cp "$FRP_CONFIG_DIR/frpc.toml" "$BREW_FRP_CONFIG/frpc.toml"
    chmod 0600 "$BREW_FRP_CONFIG/frpc.toml"

    # Check if frpc service already exists (subsequent run) or new setup
    if brew services list 2>/dev/null | grep -q "^frpc"; then
        info "Restarting frpc with new configuration..."
        if ! brew services restart frpc >/dev/null 2>&1; then
            err "Failed to restart frpc service"
            exit 1
        fi
    else
        info "Starting frpc via Homebrew services..."
        if ! brew services start frpc >/dev/null 2>&1; then
            err "Failed to start frpc service"
            exit 1
        fi
    fi
    sleep 2

    # Check if frpc is running
    if brew services list | grep -q "frpc.*started"; then
        ok "frpc is running and connected to $FRPS_ADDRESS:$FRPS_PORT"
    else
        err "frpc service failed to start"
        err "Check with: brew services list"
        exit 1
    fi
else
    # Linux: Create/update systemd unit
    sudo tee /etc/systemd/system/frpc.service > /dev/null <<EOF
[Unit]
Description=frp client (OpenMono.ai inference-box side)
After=network.target docker.service
Wants=docker.service

[Service]
Type=simple
ExecStart=/usr/local/bin/frpc -c $FRP_CONFIG_DIR/frpc.toml
Restart=on-failure
RestartSec=10s

[Install]
WantedBy=multi-user.target
EOF

    sudo systemctl daemon-reload

    # Check if frpc service was already running (subsequent setup)
    if systemctl is-active --quiet frpc 2>/dev/null; then
        info "Restarting frpc with new configuration..."
        sudo systemctl restart frpc
    else
        info "Starting frpc service..."
        sudo systemctl enable --now frpc
    fi
    sleep 2

    if systemctl is-active --quiet frpc; then
        ok "frpc is running and connected to $FRPS_ADDRESS:$FRPS_PORT"
    else
        err "frpc failed to start. Check: sudo journalctl -u frpc"
        err "Common causes: wrong relayToken, revoked token, relay unreachable, firewall blocking outbound :$FRPS_PORT"
        exit 1
    fi
fi

# ── Restart llama-server so it picks up the new API key ──────────────

if [[ "$NATIVE_INFERENCE" == "true" ]]; then
    # macOS native: source inference.sh and use native restart
    # shellcheck source=/dev/null
    source "$REPO_DIR/scripts/macos/inference.sh" 2>/dev/null || true
    if command -v native_cmd_restart &>/dev/null; then
        info "Restarting native llama-server with new API key..."
        native_cmd_restart || warn "Failed to restart llama-server"
    else
        ok "API key saved to: $ENV_FILE"
        info "Restart llama-server with: openmono restart"
    fi
else
    # Docker: try to restart via docker compose
    if command -v docker &>/dev/null && docker compose version &>/dev/null 2>&1; then
        if (cd "$REPO_DIR/docker" && docker compose ps --services 2>/dev/null | grep -q '^llama-server$'); then
            info "Restarting llama-server with new API key..."
            (cd "$REPO_DIR/docker" && docker compose up -d llama-server) || \
                warn "Restart failed — run manually: cd ${REPO_DIR}/docker && docker compose up -d llama-server"
        else
            info "llama-server not running yet. Start it with: openmono start"
        fi
    else
        ok "API key saved to: $ENV_FILE"
        info "Start llama-server with: openmono start"
    fi
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

EOF

if [[ "$OS" == "darwin" ]]; then
    cat <<EOF
${YELLOW}To check tunnel status:${NC}     brew services list
${YELLOW}To restart after config changes:${NC} brew services restart frpc
${YELLOW}To stop the service:${NC}         brew services stop frpc
${YELLOW}To tail logs:${NC}                 log stream --predicate 'process==\"frpc\"' --level debug
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
EOF
else
    cat <<EOF
${YELLOW}To check tunnel status:${NC}  sudo systemctl status frpc
${YELLOW}To tail tunnel logs:${NC}      sudo journalctl -u frpc -f
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
EOF
fi

# ── Notify relay server so the user gets connection instructions by email ──

info "Sending connection instructions to your registered email..."
CONNECT_ENDPOINT="$API_BASE/api/connection/connect"
CONNECT_HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
    -X POST "$CONNECT_ENDPOINT" \
    -H "Authorization: Bearer $RELAY_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"apiKey\": \"$LLAMA_API_KEY\"}")

if [[ "$CONNECT_HTTP_CODE" == "200" ]]; then
    ok "Connection instructions sent to your email."
else
    warn "Could not send connection email (HTTP $CONNECT_HTTP_CODE)."
    warn "You can re-send manually with:"
    warn "  curl -s -X POST $CONNECT_ENDPOINT \\"
    warn "    -H 'Authorization: Bearer \$RELAY_TOKEN' \\"
    warn "    -H 'Content-Type: application/json' \\"
    warn "    -d '{\"apiKey\": \"<your-llama-api-key>\"}'"
fi
