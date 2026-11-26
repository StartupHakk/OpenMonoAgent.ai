#!/usr/bin/env bash
# OpenMono.ai — RTX 3060 12GB profile (Ubuntu).
#
# Applied only when nvidia-smi lists an RTX 3060 with 12 GB. Other GPUs are
# untouched. Measured on echo21 (32 GB RAM, stock 170 W, then the overclock
# below): Inkling 2 Mini SQ-10, Strata 0.2.22, KV k8v8, 1 slot, reasoning
# effort low, Strata's auto drafter on. The engine keeps about 8.6 GB of
# experts in host RAM by itself; this script does not pin that split.
#
#   rtx3060-12gb.sh detect            # shell assignments, or exit 1
#   rtx3060-12gb.sh prepare           # terminal, model, settings (no sudo)
#   rtx3060-12gb.sh serve|stop|status
#   rtx3060-12gb.sh install-services  # systemd units; sudo for those units only
#   rtx3060-12gb.sh oc-restore        # stock clocks
#   rtx3060-12gb.sh oc-disable        # stock clocks and do not apply at boot
#   rtx3060-12gb.sh oc-enable         # clear a backoff latch and apply at boot
#   rtx3060-12gb.sh revert            # stop services, stock clocks, drop the marker
#   rtx3060-12gb.sh self-test
#
# Opt out of the overclock at install time with OPENMONO_RTX3060_OC=0.
# A 16 GB RAM box was not measured. detect selects that profile below 24 GiB
# and caps context at 32K with KV k8v4. Larger contexts on that box will swap.
set -euo pipefail

FEED="https://github.com/Niko1221/Strata/releases/download/v0.2.22"
MODEL_URL="https://huggingface.co/strata-engine/Inkling-2-Mini-Strata/resolve/main/Inkling-2-Mini-SQ-10.strata"
MODEL_SHA="ac4d452ad03e4930abd9d46f9c5eec22b2f75a1da45a1e49bd24ccd58b065934"
MODEL_BYTES=10471526400
MODEL_FILE_NAME="Inkling-2-Mini-SQ-10.strata"

STATE_DIR="${OPENMONO_STATE_DIR:-$HOME/.openmono}"
MARKER="$STATE_DIR/rtx3060-12gb.env"
STRATA_ENV="$STATE_DIR/strata.env"
SETTINGS="${OPENMONO_SETTINGS_FILE:-$STATE_DIR/settings.json}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKER_ENV="$REPO_DIR/docker/.env"
OC_SRC="$SCRIPT_DIR/rtx3060-oc.py"
OC_BIN="/usr/local/sbin/openmono-rtx3060-oc"
SUDOERS="/etc/sudoers.d/openmono-rtx3060-oc"
UNIT_STRATA="/etc/systemd/system/openmono-rtx3060-strata.service"
UNIT_OC="/etc/systemd/system/openmono-rtx3060-oc.service"
OC_DISABLED="/etc/openmono/rtx3060-oc.disabled"

info() { printf '[rtx3060] %s\n' "$*" >&2; }
warn() { printf '[rtx3060] %s\n' "$*" >&2; }
die()  { printf '[rtx3060] %s\n' "$*" >&2; exit 1; }

# 24 GiB of MemTotal. A 32 GB box reports about 30 GiB; a 16 GB box reports about 15.
RAM_32_KIB=$((24 * 1024 * 1024))

gpu_query() {
    if [[ -n "${RTX3060_GPU_QUERY:-}" ]]; then
        printf '%s\n' "$RTX3060_GPU_QUERY"
        return 0
    fi
    command -v nvidia-smi >/dev/null 2>&1 || return 1
    nvidia-smi --query-gpu=index,name,memory.total --format=csv,noheader,nounits 2>/dev/null || true
}

mem_kib() {
    if [[ -n "${RTX3060_MEM_KB:-}" ]]; then
        printf '%s' "$RTX3060_MEM_KB"
        return 0
    fi
    awk '/^MemTotal:/{print $2; exit}' /proc/meminfo 2>/dev/null || true
}

# stdout: MATCH line plus the RAM profile, or nothing. Exit 1 when this is not the card.
cmd_detect() {
    local query parsed mem profile ctx ctxmax kv
    query="$(gpu_query || true)"
    [[ -n "$query" ]] || return 1
    parsed="$(printf '%s\n' "$query" | awk -F',' '
        {
            idx=$1; name=$2; mem=$3
            gsub(/^[ \t]+|[ \t]+$/, "", idx)
            gsub(/^[ \t]+|[ \t]+$/, "", name)
            gsub(/^[ \t]+|[ \t]+$/, "", mem)
            lname=tolower(name)
            if (lname ~ /3060[[:space:]]*ti/ || lname ~ /3060ti/ || lname ~ /super/) next
            if (lname ~ /rtx[[:space:]]*3060/ && (mem + 0) >= 11000 && (mem + 0) <= 14000)
                print idx
        }
    ')"
    [[ -n "$parsed" ]] || return 1
    mem="$(mem_kib)"
    [[ "$mem" =~ ^[0-9]+$ ]] || mem=0
    if [[ "$mem" -ge "$RAM_32_KIB" ]]; then
        profile="32gb"
        ctx=65536
        ctxmax=131072
        kv="k8v8"
    else
        profile="16gb"
        ctx=32768
        ctxmax=32768
        kv="k8v4"
        warn "16 GB RAM profile is an estimate (not measured). Context is capped at 32K with KV k8v4. A larger context will swap."
    fi
    printf 'RTX3060_MATCH=1\n'
    printf 'RTX3060_RAM_PROFILE=%s\n' "$profile"
    printf 'RTX3060_CONTEXT=%s\n' "$ctx"
    printf 'RTX3060_CONTEXT_MAX=%s\n' "$ctxmax"
    printf 'RTX3060_KV=%s\n' "$kv"
}

load_marker() {
    # shellcheck disable=SC1090
    [[ -f "$MARKER" ]] && source "$MARKER"
    # shellcheck disable=SC1090
    [[ -f "$STRATA_ENV" ]] && source "$STRATA_ENV"
}

strata_bin() {
    if [[ -n "${STRATA_BIN:-}" && -x "$STRATA_BIN" ]]; then
        printf '%s' "$STRATA_BIN"
        return 0
    fi
    local data="${XDG_DATA_HOME:-$HOME/.local/share}/strata"
    if [[ -x "$data/terminal/current/bin/strata" ]]; then
        printf '%s' "$data/terminal/current/bin/strata"
        return 0
    fi
    return 1
}

install_terminal() {
    local data="${XDG_DATA_HOME:-$HOME/.local/share}/strata"
    local bin="$data/terminal/current/bin/strata"
    local stamp="$data/openmono-rtx3060-feed"
    if [[ -x "$bin" && -f "$stamp" ]] && [[ "$(cat "$stamp")" == "$FEED" ]]; then
        STRATA_BIN="$bin"
        info "Strata 0.2.22 terminal already installed"
        return 0
    fi
    local work
    work="$(mktemp -d "${TMPDIR:-/tmp}/rtx3060-strata.XXXXXX")"
    curl -fsSL --connect-timeout 15 --max-time 120 -o "$work/install.sh" "$FEED/install.sh" \
        || die "Could not download the Strata 0.2.22 installer"
    info "Installing Strata terminal from the 0.2.22 release (signed installer)"
    if ! STRATA_FEED="$FEED" sh "$work/install.sh"; then
        rm -rf "$work"
        die "Strata 0.2.22 installer failed"
    fi
    rm -rf "$work"
    [[ -x "$bin" ]] || die "Strata binary missing at $bin"
    mkdir -p "$data"
    printf '%s\n' "$FEED" > "$stamp"
    STRATA_BIN="$bin"
    info "Strata terminal: $STRATA_BIN"
}

model_path() {
    printf '%s' "${RTX3060_MODEL_PATH:-$STATE_DIR/models/$MODEL_FILE_NAME}"
}

install_model() {
    local dest dir
    dest="$(model_path)"
    dir="$(dirname "$dest")"
    mkdir -p "$dir"
    if [[ -f "$dest" ]]; then
        local got
        got="$(sha256sum "$dest" | awk '{print $1}')"
        if [[ "$got" == "$MODEL_SHA" ]]; then
            info "Inkling SQ-10 already present ($(du -h "$dest" | awk '{print $1}'))"
            RTX3060_MODEL_PATH="$dest"
            return 0
        fi
        warn "Model checksum mismatch — downloading again"
        rm -f "$dest"
    fi
    info "Downloading Inkling 2 Mini SQ-10 (10.47 GB)"
    if ! curl -fL --retry 3 --retry-delay 2 -o "$dest.partial" "$MODEL_URL"; then
        rm -f "$dest.partial"
        die "Inkling SQ-10 download failed"
    fi
    local got
    got="$(sha256sum "$dest.partial" | awk '{print $1}')"
    if [[ "$got" != "$MODEL_SHA" ]]; then
        rm -f "$dest.partial"
        die "Inkling SQ-10 checksum mismatch (got $got)"
    fi
    mv "$dest.partial" "$dest"
    RTX3060_MODEL_PATH="$dest"
    info "Inkling SQ-10 checksum ok"
}

read_api_key() {
    local key=""
    if [[ -f "$DOCKER_ENV" ]]; then
        key="$(sed -n 's/^LLAMA_API_KEY=//p' "$DOCKER_ENV" | head -1)"
    fi
    if [[ -z "$key" && -f "$SETTINGS" ]] && command -v jq >/dev/null 2>&1; then
        key="$(jq -r '.llm.api_key // empty' "$SETTINGS" 2>/dev/null || true)"
    fi
    printf '%s' "$key"
}

write_state() {
    local profile="$1" ctx="$2" ctxmax="$3" kv="$4" bin model port
    bin="$STRATA_BIN"
    model="$(model_path)"
    port="${INFERENCE_PORT:-7474}"
    mkdir -p "$STATE_DIR" "$(dirname "$model")"
    if [[ -f "$STRATA_ENV" ]] && ! grep -q '^STRATA_FLAVOR="cli-0.2.22"$' "$STRATA_ENV"; then
        cp -a "$STRATA_ENV" "$STRATA_ENV.bak-before-rtx3060"
        info "Backed up the previous Strata env to $STRATA_ENV.bak-before-rtx3060"
    fi
    cat > "$STRATA_ENV" <<EOF
STRATA_FLAVOR="cli-0.2.22"
STRATA_BIN="$bin"
STRATA_FEED="$FEED"
INFERENCE_PORT="$port"
INFERENCE_PARALLEL="1"
STRATA_FAMILY="inkling-2-mini"
STRATA_MODEL="Inkling-2-Mini-SQ-10"
STRATA_CONTEXT="$ctx"
STRATA_CONTEXT_MAX="$ctxmax"
STRATA_KV="$kv"
STRATA_VISION="no"
STRATA_DRAFTING="auto"
STRATA_DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/strata"
RTX3060_MODEL_PATH="$model"
EOF
    chmod 0600 "$STRATA_ENV"
    local oc="1"
    [[ "${OPENMONO_RTX3060_OC:-1}" == "0" ]] && oc="0"
    cat > "$MARKER" <<EOF
RTX3060_PROFILE=1
RTX3060_RAM_PROFILE=$profile
STRATA_FLAVOR=cli-0.2.22
STRATA_CONTEXT=$ctx
STRATA_CONTEXT_MAX=$ctxmax
STRATA_KV=$kv
OC_ENABLED=$oc
EOF
    chmod 0600 "$MARKER"
    command -v jq >/dev/null 2>&1 || die "jq is required to write $SETTINGS"
    [[ -f "$SETTINGS" ]] || echo '{}' > "$SETTINGS"
    local tmp
    tmp="$(mktemp "${SETTINGS}.tmp.XXXXXX")"
    jq --arg ep "http://127.0.0.1:${port}/v1" \
       --arg model "inkling-2-mini-sq-10" \
       --argjson ctx "$ctx" --argjson ctxmax "$ctxmax" --argjson port "$port" \
       '.llm = (.llm // {})
        | .llm.endpoint = $ep
        | .llm.model = $model
        | .llm.reasoning_effort = "low"
        | .llm.context_size = $ctx
        | .inference = (.inference // {})
        | .inference.ctx_size = $ctx
        | .inference.ctx_max = $ctxmax
        | .inference.port = $port
        | .inference.parallel_slots = 1
        | .inference.vision_enabled = false' \
        "$SETTINGS" > "$tmp"
    mv "$tmp" "$SETTINGS"
    chmod 0600 "$SETTINGS"
    info "Profile $profile written (context $ctx, max $ctxmax, KV $kv, reasoning_effort low)"
}

cmd_prepare() {
    local detect_out
    detect_out="$(cmd_detect)" || die "This machine is not an RTX 3060 12GB. Nothing was changed."
    # shellcheck disable=SC2086
    eval "$detect_out"
    install_terminal
    install_model
    write_state "$RTX3060_RAM_PROFILE" "$RTX3060_CONTEXT" "$RTX3060_CONTEXT_MAX" "$RTX3060_KV"
    info "Strata is not started yet. openmono setup starts it, or: bash scripts/rtx3060-12gb.sh serve"
}

context_flag() {
    local ctx="${1:-65536}"
    case "$ctx" in
        32768) printf '32k' ;;
        65536) printf '64k' ;;
        131072) printf '128k' ;;
        *) printf '%s' "$ctx" ;;
    esac
}

clamp_context() {
    local ctx="$1" max="${2:-131072}"
    if [[ "$ctx" =~ ^[0-9]+$ && "$max" =~ ^[0-9]+$ && "$ctx" -gt "$max" ]]; then
        warn "Context $ctx is above the profile cap $max. Using $max."
        ctx="$max"
    fi
    printf '%s' "$ctx"
}

healthy() {
    local port="$1" key code body
    key="$(read_api_key)"
    if [[ -n "$key" ]]; then
        code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 -H "Authorization: Bearer $key" "http://127.0.0.1:${port}/health" || true)"
        body="$(curl -s --max-time 3 -H "Authorization: Bearer $key" "http://127.0.0.1:${port}/v1/models" || true)"
    else
        code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "http://127.0.0.1:${port}/health" || true)"
        body="$(curl -s --max-time 3 "http://127.0.0.1:${port}/v1/models" || true)"
    fi
    [[ "$code" == "200" ]] && return 0
    [[ "$body" == *loaded* || "$body" == *'"id"'* ]]
}

cmd_serve() {
    load_marker
    local bin port ctx kv model data key
    bin="$(strata_bin)" || die "Strata 0.2.22 is not installed. Run: bash scripts/rtx3060-12gb.sh prepare"
    port="${INFERENCE_PORT:-7474}"
    ctx="$(clamp_context "${STRATA_CONTEXT:-65536}" "${STRATA_CONTEXT_MAX:-131072}")"
    kv="${STRATA_KV:-k8v8}"
    model="${RTX3060_MODEL_PATH:-$(model_path)}"
    data="${STRATA_DATA_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/strata}"
    [[ -f "$model" ]] || die "Model file missing: $model"
    if healthy "$port"; then
        info "Strata already serving on 127.0.0.1:${port}"
        return 0
    fi
    key="$(read_api_key)"
    local -a args
    args=(
        "$bin" launch
        --model-path "$model"
        --family inkling-2-mini
        --data-dir "$data"
        --context "$(context_flag "$ctx")"
        --kv "$kv"
        --vision off
        --parallel "${INFERENCE_PARALLEL:-1}"
        --drafting "${STRATA_DRAFTING:-auto}"
        --tune skip
        --access lan
        --port "$port"
        --feed "${STRATA_FEED:-$FEED}"
        --restart --yes --no-tui
    )
    if [[ -n "$key" ]]; then
        export LLAMA_API_KEY="$key"
        args+=(--api-key-env LLAMA_API_KEY)
    else
        warn "No LLAMA_API_KEY yet — starting without an API key. Run openmono start once so a key is generated, then restart Strata."
        args+=(--no-api-key)
    fi
    mkdir -p "$STATE_DIR"
    info "Starting Inkling SQ-10 (context $(context_flag "$ctx"), KV $kv, 1 slot, auto drafter)"
    "${args[@]}" >> "$STATE_DIR/strata-server.log" 2>&1 || die "strata launch failed (see $STATE_DIR/strata-server.log)"
    local i
    for i in $(seq 1 180); do
        healthy "$port" && break
        sleep 5
    done
    healthy "$port" || die "Strata did not become healthy on port $port (see $STATE_DIR/strata-server.log)"
    info "Strata is healthy at http://127.0.0.1:${port}/v1"
}

cmd_stop() {
    load_marker || true
    local bin
    if bin="$(strata_bin)"; then
        "$bin" launch --stop >> "$STATE_DIR/strata-server.log" 2>&1 || true
        info "Strata stop requested"
    else
        info "Strata terminal is not installed; nothing to stop"
    fi
}

cmd_status() {
    load_marker || true
    local port="${INFERENCE_PORT:-7474}"
    if healthy "$port"; then
        info "healthy http://127.0.0.1:${port}/v1"
        return 0
    fi
    info "not responding on 127.0.0.1:${port}"
    return 1
}

sudo_run() {
    if [[ "$(id -u)" -eq 0 ]]; then
        "$@"
        return
    fi
    if [[ -t 0 ]]; then
        sudo "$@"
    else
        sudo -n "$@"
    fi
}

install_oc_helper() {
    [[ "${OPENMONO_RTX3060_OC:-1}" != "0" ]] || { info "OPENMONO_RTX3060_OC=0 — not installing the overclock"; return 0; }
    local user group home
    user="${SUDO_USER:-$USER}"
    home="$(getent passwd "$user" | cut -d: -f6)"
    group="$(id -gn "$user")"
    local tmp
    tmp="$(mktemp)"
    cat > "$tmp" <<EOF
# OpenMono RTX 3060 overclock. This rule allows ONLY the helper's four commands.
$user ALL=(root) NOPASSWD: $OC_BIN status, $OC_BIN apply, $OC_BIN restore, $OC_BIN watch
EOF
    sudo_run install -o root -g root -m 0755 "$OC_SRC" "$OC_BIN"
    sudo_run visudo -cf "$tmp"
    sudo_run install -o root -g root -m 0440 "$tmp" "$SUDOERS"
    rm -f "$tmp"
    sudo_run tee "$UNIT_OC" >/dev/null <<'EOF'
[Unit]
Description=OpenMono RTX 3060 12GB overclock (212 W, core +150 MHz, memory +2000 MHz)
After=systemd-modules-load.service
ConditionPathExists=!/etc/openmono/rtx3060-oc.disabled

[Service]
Type=simple
ExecStart=/usr/local/sbin/openmono-rtx3060-oc watch
ExecStop=/usr/local/sbin/openmono-rtx3060-oc restore
Restart=no

[Install]
WantedBy=multi-user.target
EOF
    info "Overclock helper installed (sudo is limited to $OC_BIN)"
    # home/group are recorded so a later reader can see who installed it.
    info "Overclock service will run as root for NVML; installed for $user ($group, $home)"
}

install_strata_unit() {
    local user group home
    user="${SUDO_USER:-$USER}"
    home="$(getent passwd "$user" | cut -d: -f6)"
    group="$(id -gn "$user")"
    sudo_run tee "$UNIT_STRATA" >/dev/null <<EOF
[Unit]
Description=OpenMono Strata 0.2.22 (Inkling 2 Mini) for RTX 3060 12GB
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
User=$user
Group=$group
Environment=HOME=$home
WorkingDirectory=$home
ExecStart=$SCRIPT_DIR/rtx3060-12gb.sh serve
ExecStop=$SCRIPT_DIR/rtx3060-12gb.sh stop
TimeoutStartSec=900

[Install]
WantedBy=multi-user.target
EOF
}

cmd_install_services() {
    [[ -f "$MARKER" ]] || die "Profile is not prepared. Run: bash scripts/rtx3060-12gb.sh prepare"
    install_strata_unit
    if [[ "${OPENMONO_RTX3060_OC:-1}" == "0" ]]; then
        sudo_run mkdir -p /etc/openmono
        sudo_run touch "$OC_DISABLED"
        info "Overclock opt-out recorded at $OC_DISABLED"
    else
        install_oc_helper
        sudo_run rm -f "$OC_DISABLED"
    fi
    sudo_run systemctl daemon-reload
    sudo_run systemctl enable --now openmono-rtx3060-strata.service
    if [[ "${OPENMONO_RTX3060_OC:-1}" != "0" ]]; then
        sudo_run systemctl enable --now openmono-rtx3060-oc.service \
            || warn "Overclock service did not start. Clocks stay at stock. Check: journalctl -u openmono-rtx3060-oc"
    fi
    info "Boot services enabled"
}

cmd_oc_restore() {
    if [[ ! -x "$OC_BIN" ]]; then
        die "Overclock helper is not installed at $OC_BIN"
    fi
    sudo_run "$OC_BIN" restore
    info "GPU restored to stock (default power limit, clock offsets 0)"
}

cmd_oc_disable() {
    sudo_run mkdir -p /etc/openmono
    sudo_run touch "$OC_DISABLED"
    if [[ -x "$OC_BIN" ]]; then
        sudo_run "$OC_BIN" restore || true
    fi
    sudo_run systemctl disable --now openmono-rtx3060-oc.service 2>/dev/null || true
    info "Overclock disabled. Stock clocks. The file $OC_DISABLED keeps it off across reboots."
    info "Turn it back on with: bash scripts/rtx3060-12gb.sh oc-enable"
}

cmd_oc_enable() {
    sudo_run rm -f "$OC_DISABLED" /var/lib/openmono/rtx3060-oc.backed-off
    [[ -x "$OC_BIN" ]] || die "Overclock helper is not installed. Re-run: bash scripts/rtx3060-12gb.sh install-services"
    sudo_run systemctl enable --now openmono-rtx3060-oc.service
    info "Overclock enabled (212 W, core +150 MHz, memory +2000 MHz)"
}

cmd_revert() {
    cmd_stop || true
    if [[ -x "$OC_BIN" ]]; then
        sudo_run "$OC_BIN" restore || true
    fi
    sudo_run systemctl disable --now openmono-rtx3060-strata.service 2>/dev/null || true
    sudo_run systemctl disable --now openmono-rtx3060-oc.service 2>/dev/null || true
    rm -f "$MARKER"
    info "Profile marker removed. Strata is stopped and clocks are stock if the helper was installed."
    info "docker/.env is left as INFERENCE_BACKEND=strata so the old llama.cpp container is not started."
    info "The model file is kept. To install the stock model for this card, check out main and run: openmono setup"
    info "To remove the helper itself: sudo rm $SUDOERS $OC_BIN $UNIT_STRATA $UNIT_OC $OC_DISABLED && sudo systemctl daemon-reload"
}

cmd_self_test() {
    local out
    out="$(RTX3060_GPU_QUERY='0, NVIDIA GeForce RTX 3060, 12288' RTX3060_MEM_KB=30000000 bash "$0" detect)"
    printf '%s\n' "$out" | grep -qx 'RTX3060_MATCH=1'
    printf '%s\n' "$out" | grep -qx 'RTX3060_RAM_PROFILE=32gb'
    printf '%s\n' "$out" | grep -qx 'RTX3060_CONTEXT=65536'
    printf '%s\n' "$out" | grep -qx 'RTX3060_CONTEXT_MAX=131072'
    printf '%s\n' "$out" | grep -qx 'RTX3060_KV=k8v8'
    out="$(RTX3060_GPU_QUERY='0, NVIDIA GeForce RTX 3060, 12288' RTX3060_MEM_KB=15000000 bash "$0" detect 2>/dev/null)"
    printf '%s\n' "$out" | grep -qx 'RTX3060_RAM_PROFILE=16gb'
    printf '%s\n' "$out" | grep -qx 'RTX3060_CONTEXT=32768'
    printf '%s\n' "$out" | grep -qx 'RTX3060_KV=k8v4'
    if RTX3060_GPU_QUERY='0, NVIDIA GeForce RTX 3060 Ti, 8192' RTX3060_MEM_KB=30000000 bash "$0" detect >/dev/null 2>&1; then
        die "self-test: 3060 Ti must not match"
    fi
    if RTX3060_GPU_QUERY='0, NVIDIA GeForce RTX 4090, 24576' RTX3060_MEM_KB=60000000 bash "$0" detect >/dev/null 2>&1; then
        die "self-test: 4090 must not match"
    fi
    if RTX3060_GPU_QUERY='1, NVIDIA GeForce RTX 3060, 12288' RTX3060_MEM_KB=30000000 bash "$0" detect >/dev/null 2>&1; then
        :
    else
        die "self-test: a 3060 that is not GPU 0 must still match"
    fi
    python3 "$OC_SRC" self-test
    info "self-test ok"
}

usage() {
    sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'
}

case "${1:-help}" in
    detect)            cmd_detect ;;
    prepare)           cmd_prepare ;;
    serve)             cmd_serve ;;
    stop)              cmd_stop ;;
    status)            cmd_status ;;
    install-services)  cmd_install_services ;;
    oc-restore)        cmd_oc_restore ;;
    oc-disable)        cmd_oc_disable ;;
    oc-enable)         cmd_oc_enable ;;
    revert)            cmd_revert ;;
    self-test)         cmd_self_test ;;
    help|--help|-h)    usage ;;
    *) die "Unknown command: $1 (try --help)" ;;
esac
