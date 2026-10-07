#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Strata inference backend manager (Linux only)
#
# Strata (https://github.com/Niko1221/Strata, MIT License) runs the
# Qwen3.8-Flash-Next model on a regular gaming PC (NVIDIA/AMD GPU, 12GB+ VRAM)
# and serves an OpenAI-compatible API at http://127.0.0.1:8080/v1
# (plus /v1/messages for Anthropic-style clients and /v1/responses).
#
# OpenMono does NOT vendor Strata. This script clones upstream Strata next to
# the install (default ~/strata), drives its documented Linux flow
# (./setup.sh --check / --yes ...), and manages the server process it writes
# (run-<model>.sh). The .NET agent then talks to Strata exactly like any other
# OpenAI-compatible provider — any API key and any model name work.
#
# State: ~/.openmono/strata.env  (STRATA_DIR, STRATA_PORT, STRATA_MODEL, ...)
#
# Usage:
#   strata.sh check            # hardware preflight (no changes besides Strata's own python venv probe)
#   strata.sh install [--yes] [--family qwen --model IQ2_XS ...]  # clone + ./setup.sh --no-start
#   strata.sh start            # launch run-<model>.sh detached, wait for /health
#   strata.sh stop             # stop the server process
#   strata.sh status           # /health + /v1/models summary (for `openmono status`)
#   strata.sh logs [--tail N]  # tail the server log
#   strata.sh update           # git pull + ./update.sh (no start)
#
# Env overrides:
#   STRATA_DIR     where Strata lives            (default ~/strata)
#   STRATA_PORT    server port                   (default 8080)
#   STRATA_FAMILY  qwen|swift|coder|unsloth      (default qwen)
#   STRATA_MODEL   Q2_0|IQ2_XS|IQ3_XXS|IQ3_S|... (default: setup picks for the RAM)
#   STRATA_CONTEXT context tokens                (default: setup picks for the VRAM)
#   STRATA_VISION  yes|no|cpu                    (default no)
#   STRATA_DATA_DIR model-file dir               (default <STRATA_DIR>-data next to Strata)
# ──────────────────────────────────────────────────────────────────────────────

STRATA_REPO="https://github.com/Niko1221/Strata.git"
STRATA_DIR="${STRATA_DIR:-$HOME/strata}"
STRATA_PORT="${STRATA_PORT:-8080}"
STRATA_FAMILY="${STRATA_FAMILY:-qwen}"
STRATA_MODEL="${STRATA_MODEL:-}"
STRATA_CONTEXT="${STRATA_CONTEXT:-}"
STRATA_VISION="${STRATA_VISION:-no}"
STRATA_DATA_DIR="${STRATA_DATA_DIR:-}"
OPENMONO_STATE_DIR="${OPENMONO_STATE_DIR:-$HOME/.openmono}"
STRATA_ENV_FILE="$OPENMONO_STATE_DIR/strata.env"
STRATA_PID_FILE="$OPENMONO_STATE_DIR/strata.pid"
STRATA_LOG="$OPENMONO_STATE_DIR/strata-server.log"

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'
BLUE=$'\033[38;2;163;255;102m'; NC=$'\033[0m'
info() { echo -e "${BLUE}[strata]${NC} $*"; }
ok()   { echo -e "${GREEN}[strata]${NC} $*"; }
warn() { echo -e "${YELLOW}[strata]${NC} $*" >&2; }
err()  { echo -e "${RED}[strata]${NC} $*" >&2; }

if [[ "$(uname -s)" != "Linux" ]]; then
    err "Strata backend is Linux-only in OpenMono (macOS keeps its native inference path)."
    exit 1
fi

save_env() {
    mkdir -p "$OPENMONO_STATE_DIR"
    cat > "$STRATA_ENV_FILE" <<EOF
STRATA_DIR="$STRATA_DIR"
STRATA_PORT="$STRATA_PORT"
STRATA_FAMILY="$STRATA_FAMILY"
STRATA_MODEL="$STRATA_MODEL"
STRATA_CONTEXT="$STRATA_CONTEXT"
STRATA_VISION="$STRATA_VISION"
STRATA_DATA_DIR="$STRATA_DATA_DIR"
EOF
}

load_env() {
    # shellcheck source=/dev/null
    [[ -f "$STRATA_ENV_FILE" ]] && source "$STRATA_ENV_FILE"
    STRATA_DIR="${STRATA_DIR:-$HOME/strata}"
    STRATA_PORT="${STRATA_PORT:-8080}"
}

strata_base_url() { echo "http://127.0.0.1:${STRATA_PORT}"; }

ensure_clone() {
    if [[ -f "$STRATA_DIR/setup.sh" ]]; then
        return 0
    fi
    if [[ -e "$STRATA_DIR" && ! -d "$STRATA_DIR" ]]; then
        err "$STRATA_DIR exists and is not a directory."
        exit 1
    fi
    info "Cloning Strata ($STRATA_REPO) into $STRATA_DIR ..."
    info "Upstream project is MIT-licensed; see $STRATA_DIR/LICENSE after clone."
    git clone "$STRATA_REPO" "$STRATA_DIR" \
        || { err "git clone failed — check network access to github.com"; exit 1; }
    ok "Strata cloned."
}

# Resolve the run script Strata's setup wrote (run-<family-tag>_<size>.sh, lower case).
find_run_script() {
    local s
    s="$(ls -t "$STRATA_DIR"/run-*.sh 2>/dev/null | head -1 || true)"
    echo "$s"
}

cmd_check() {
    ensure_clone
    info "Running Strata hardware preflight (./setup.sh --check) ..."
    (cd "$STRATA_DIR" && ./setup.sh --check)
}

cmd_install() {
    # Extra flags after `install` are forwarded to Strata's setup.sh verbatim,
    # e.g.: strata.sh install --yes --family coder
    ensure_clone
    local extra=("$@")
    local setup_args=()
    [[ "${STRATA_YES:-0}" == "1" ]] && setup_args+=(--yes)
    [[ -n "$STRATA_FAMILY" ]] && setup_args+=(--family "$STRATA_FAMILY")
    [[ -n "$STRATA_MODEL" ]] && setup_args+=(--model "$STRATA_MODEL")
    [[ -n "$STRATA_CONTEXT" ]] && setup_args+=(--context "$STRATA_CONTEXT")
    [[ -n "$STRATA_VISION" ]] && setup_args+=(--vision "$STRATA_VISION")
    [[ "$STRATA_PORT" != "8080" ]] && setup_args+=(--port "$STRATA_PORT")
    [[ -n "$STRATA_DATA_DIR" ]] && setup_args+=(--data-dir "$STRATA_DATA_DIR")
    setup_args+=(--no-start)
    info "Installing Strata model (this downloads ~70 GB on first run; resumable) ..."
    info "  setup args: ${setup_args[*]} ${extra[*]}"
    info "  Strata will prompt unless --yes was passed (non-interactive installs add it)."
    (cd "$STRATA_DIR" && ./setup.sh "${setup_args[@]}" "${extra[@]}")
    save_env
    ok "Strata installed. Start it with: openmono start   (or scripts/strata.sh start)"
    ok "Browse the model UI at: $(strata_base_url)  (after start)"
}

server_wait_healthy() {
    local tries="${1:-36}" url
    url="$(strata_base_url)/health"
    for ((i = 1; i <= tries; i++)); do
        if curl -sf "$url" &>/dev/null; then
            return 0
        fi
        sleep 5
        printf "."
    done
    return 1
}

cmd_start() {
    load_env
    if [[ ! -f "$STRATA_DIR/setup.sh" ]]; then
        err "Strata is not installed at $STRATA_DIR. Run: openmono setup   (or scripts/strata.sh install)"
        exit 1
    fi
    if curl -sf "$(strata_base_url)/health" &>/dev/null; then
        ok "Strata already serving at $(strata_base_url)"
        return 0
    fi
    local run_script
    run_script="$(find_run_script)"
    if [[ -z "$run_script" ]]; then
        err "No run-*.sh found in $STRATA_DIR — install first: scripts/strata.sh install"
        exit 1
    fi
    info "Starting Strata ($run_script) detached ..."
    mkdir -p "$OPENMONO_STATE_DIR"
    # Strata's run script is foreground-oriented; detach so the shell returns.
    nohup bash "$run_script" > "$STRATA_LOG" 2>&1 &
    echo "$!" > "$STRATA_PID_FILE"
    info "Waiting for $(strata_base_url)/health (first boot loads 35-55 GB; PC may feel slow for 1-3 min) ..."
    if server_wait_healthy 36; then
        echo ""
        ok "Strata is healthy at $(strata_base_url)  (OpenAI base: $(strata_base_url)/v1)"
    else
        echo ""
        warn "Not healthy after ~3 min — model may still be loading on first boot."
        warn "Watch: tail -f $STRATA_LOG   (engine detail: $STRATA_DIR/strata-*.log)"
    fi
}

cmd_stop() {
    load_env
    local stopped=0
    if [[ -f "$STRATA_PID_FILE" ]]; then
        local pid
        pid="$(cat "$STRATA_PID_FILE" 2>/dev/null || true)"
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            info "Stopping Strata (pid $pid) ..."
            kill "$pid" 2>/dev/null || true
            for _ in $(seq 1 15); do
                kill -0 "$pid" 2>/dev/null || { stopped=1; break; }
                sleep 1
            done
            kill -9 "$pid" 2>/dev/null || true
            stopped=1
        fi
        rm -f "$STRATA_PID_FILE"
    fi
    # Fallback: anything still listening on the port (covers servers started by hand).
    if curl -sf "$(strata_base_url)/health" &>/dev/null; then
        warn "Server still responding on $(strata_base_url) — it was started outside strata.sh; stop that process to free port $STRATA_PORT."
        return 1
    fi
    [[ "$stopped" == "1" ]] && ok "Strata stopped." || info "Strata is not running."
}

cmd_status() {
    load_env
    local url models
    url="$(strata_base_url)"
    echo -n "strata ($url): "
    if curl -sf "$url/health" 2>/dev/null | python3 -c "import sys,json; d=json.load(sys.stdin); print(('HEALTHY model=' + str(d.get('model','?')) + ' ctx=' + str(d.get('max_context','?'))) if d.get('loaded') else 'LOADING')" 2>/dev/null; then
        true
    else
        echo -e "${RED}UNREACHABLE${NC}"
    fi
    echo -n "OpenAI endpoint ($url/v1): "
    models="$(curl -sf "$url/v1/models" 2>/dev/null || true)"
    if [[ -n "$models" ]]; then
        echo "$models" | python3 -c "import sys,json; d=json.load(sys.stdin); print(', '.join(m.get('id','?') for m in d.get('data',[])) or 'reachable (no models listed)')" 2>/dev/null \
            || echo "reachable"
    else
        echo -e "${RED}unreachable${NC}"
    fi
}

cmd_logs() {
    local tail_n=100
    [[ "${1:-}" == "--tail" && -n "${2:-}" ]] && tail_n="$2"
    if [[ -f "$STRATA_LOG" ]]; then
        tail -n "$tail_n" -f "$STRATA_LOG"
    else
        warn "No wrapper log yet at $STRATA_LOG."
        warn "Strata engine logs live in $STRATA_DIR/strata-*.log (after the first start)."
        ls -t "$STRATA_DIR"/strata-*.log 2>/dev/null | head -5 || true
        exit 1
    fi
}

cmd_update() {
    ensure_clone
    info "Updating Strata (git pull + ./update.sh, server not started) ..."
    (cd "$STRATA_DIR" && git pull --ff-only || warn "git pull failed — continuing with ./update.sh")
    (cd "$STRATA_DIR" && ./update.sh)
    ok "Strata updated."
}

case "${1:-help}" in
    check)   shift; cmd_check "$@" ;;
    install) shift; cmd_install "$@" ;;
    start)   shift; cmd_start "$@" ;;
    stop)    shift; cmd_stop "$@" ;;
    status)  shift; cmd_status "$@" ;;
    logs)    shift; cmd_logs "$@" ;;
    update)  shift; cmd_update "$@" ;;
    help|--help|-h|*)
        sed -n '2,/^# ─*$/p' "$0" | sed 's/^# \?//'
        ;;
esac
