#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai - Mosaic inference backend manager (Linux + NVIDIA)
#
# Mosaic is OpenMono's Strata-style MoE runtime for families Strata cannot
# pack (see docs/MOE-ENGINE.md). First family: Xiaomi MiMo-V2.6-Flash-RL.
# Qwen3.8-Flash-Next stays on scripts/strata.sh. Mosaic does not vendor
# Strata or PolyStrata kernels.
#
# Hard rule: after startup pin, decode never reads weights from NVMe/SSD.
# mmap paging is refused (MOSAIC_ALLOW_MMAP=0).
#
# Usage:
#   mosaic.sh check
#   mosaic.sh install [--yes]
#   mosaic.sh start
#   mosaic.sh stop
#   mosaic.sh status
#   mosaic.sh logs [--tail N]
#
# Env:
#   MOSAIC_PORT       OpenAI port (default 8090; Strata keeps 8080)
#   MOSAIC_CONTEXT    context tokens (default 131072)
#   MOSAIC_PACK_DIR   directory of OMS1 blobs (default ~/.openmono/mosaic-pack)
#   MOSAIC_ALLOW_MMAP must stay 0
# ──────────────────────────────────────────────────────────────────────────────

MOSAIC_PORT="${MOSAIC_PORT:-8090}"
MOSAIC_CONTEXT="${MOSAIC_CONTEXT:-131072}"
MOSAIC_FAMILY="${MOSAIC_FAMILY:-mimo-v2.6-flash}"
MOSAIC_ALLOW_MMAP="${MOSAIC_ALLOW_MMAP:-0}"
OPENMONO_STATE_DIR="${OPENMONO_STATE_DIR:-$HOME/.openmono}"
MOSAIC_ENV_FILE="$OPENMONO_STATE_DIR/mosaic.env"
MOSAIC_PID_FILE="$OPENMONO_STATE_DIR/mosaic.pid"
MOSAIC_LOG="$OPENMONO_STATE_DIR/mosaic-server.log"
MOSAIC_PACK_DIR="${MOSAIC_PACK_DIR:-$OPENMONO_STATE_DIR/mosaic-pack}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKER_ENV_FILE="$REPO_DIR/docker/.env"

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'
BLUE=$'\033[38;2;163;255;102m'; NC=$'\033[0m'
info() { echo -e "${BLUE}[mosaic]${NC} $*"; }
ok()   { echo -e "${GREEN}[mosaic]${NC} $*"; }
warn() { echo -e "${YELLOW}[mosaic]${NC} $*" >&2; }
err()  { echo -e "${RED}[mosaic]${NC} $*" >&2; }

if [[ "$(uname -s)" != "Linux" ]]; then
    err "Mosaic is Linux + NVIDIA only (macOS keeps its native llama.cpp path)."
    exit 1
fi

if [[ "$MOSAIC_ALLOW_MMAP" != "0" ]]; then
    err "MOSAIC_ALLOW_MMAP=$MOSAIC_ALLOW_MMAP is refused. Weights must be pinned in RAM."
    exit 1
fi

save_env() {
    mkdir -p "$OPENMONO_STATE_DIR"
    cat > "$MOSAIC_ENV_FILE" <<EOF
MOSAIC_PORT="$MOSAIC_PORT"
MOSAIC_CONTEXT="$MOSAIC_CONTEXT"
MOSAIC_FAMILY="$MOSAIC_FAMILY"
MOSAIC_PACK_DIR="$MOSAIC_PACK_DIR"
MOSAIC_ALLOW_MMAP="0"
EOF
}

load_env() {
    # shellcheck source=/dev/null
    [[ -f "$MOSAIC_ENV_FILE" ]] && source "$MOSAIC_ENV_FILE"
    MOSAIC_PORT="${MOSAIC_PORT:-8090}"
    MOSAIC_CONTEXT="${MOSAIC_CONTEXT:-131072}"
    MOSAIC_PACK_DIR="${MOSAIC_PACK_DIR:-$OPENMONO_STATE_DIR/mosaic-pack}"
}

mosaic_base_url() { echo "http://127.0.0.1:${MOSAIC_PORT}"; }

_vram_mb() {
    if command -v nvidia-smi &>/dev/null; then
        nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | awk 'NR==1{print $1}'
    else
        echo 0
    fi
}

_mem_gb() {
    awk '/^MemTotal:/{printf "%d", $2/1048576}' /proc/meminfo 2>/dev/null || echo 0
}

_has_nvidia() {
    if command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null; then return 0; fi
    if command -v lspci &>/dev/null && lspci 2>/dev/null | grep -qi nvidia; then return 0; fi
    [[ -e /proc/driver/nvidia ]] && return 0
    return 1
}

cmd_check() {
    local rc=0
    local vram mem
    vram="$(_vram_mb)"
    mem="$(_mem_gb)"
    info "Preflight: NVIDIA 24GB+ VRAM, 64GB-class RAM, no mmap."
    if ! _has_nvidia; then
        err "No NVIDIA GPU visible. Mosaic is gated on CUDA devices."
        rc=1
    else
        ok "NVIDIA device detected"
    fi
    if [[ "${vram:-0}" -lt 24000 ]]; then
        err "VRAM ${vram:-0} MiB is below the 24 GB gate (need >= 24000 MiB)."
        rc=1
    else
        ok "VRAM: ${vram} MiB"
    fi
    if [[ "${mem:-0}" -lt 60 ]]; then
        err "RAM ${mem:-0} GiB is below the 64GB-class gate (need >= 60 GiB reported)."
        rc=1
    else
        ok "RAM: ${mem} GiB"
    fi
    if [[ "$MOSAIC_ALLOW_MMAP" != "0" ]]; then
        err "mmap is enabled; refuse to continue."
        rc=1
    else
        ok "mmap disabled (MOSAIC_ALLOW_MMAP=0)"
    fi
    info "Quality-max pack is REAP50 mixed (docs/MIMO.md config A). Official Flash-RL is 165.56 GiB and will not fit."
    info "Docs: $REPO_DIR/docs/MIMO.md"
    return "$rc"
}

cmd_install() {
    info "Installing Mosaic state (no 60+ GB download on this step)."
    mkdir -p "$MOSAIC_PACK_DIR" "$OPENMONO_STATE_DIR"
    save_env
    if [[ ! -f "$MOSAIC_PACK_DIR/README.txt" ]]; then
        cat > "$MOSAIC_PACK_DIR/README.txt" <<EOF
Mosaic pack directory (OMS1 blobs).
Hot path: pin these files into the RAM arena at startup, then never read them again.
Recommended (docs/MIMO.md config A):
  - patrickbdevaney/MiMo-V2.6-Flash-REAP50-GGUF  MiMo-V2.6-Flash-REAP50-Q2_K.gguf  (61.64 GiB, measured)
  - mtp-MiMo-V2.6-Flash-REAP50-Q8_0.gguf (2.22 GiB, measured)
  - skip mmproj (text-only agent)
Convert into OMS1 with the OpenMono.Moe packer on a GPU box. Do not mmap the GGUF.
EOF
    fi
    ok "Mosaic env written to $MOSAIC_ENV_FILE"
    ok "Pack dir: $MOSAIC_PACK_DIR"
    info "Next: place the REAP50 pack, then: bash scripts/mosaic.sh start"
    info "Needs a real GPU box for decode. See docs/MIMO.md."
}

cmd_start() {
    load_env
    if [[ "$MOSAIC_ALLOW_MMAP" != "0" ]]; then
        err "Refusing to start with mmap enabled."
        return 1
    fi
    if curl -sf "$(mosaic_base_url)/health" &>/dev/null; then
        ok "Mosaic already serving at $(mosaic_base_url)"
        return 0
    fi
    if [[ ! -d "$MOSAIC_PACK_DIR" ]]; then
        err "Pack dir missing. Run: bash scripts/mosaic.sh install"
        return 1
    fi
    # This milestone does not bind a CUDA server on the VM. A GPU box will
    # launch the real host process here. We record intent and fail closed
    # unless a health endpoint is already up.
    warn "Mosaic HTTP server is not in this milestone (CPU scaffold + tests only)."
    warn "On a 24GB+ / 64GB+ NVIDIA box: convert the REAP50 pack to OMS1, pin it,"
    warn "and serve OpenAI /v1 on $(mosaic_base_url). Checklist: docs/MIMO.md"
    echo "$(date -Is) mosaic start requested family=$MOSAIC_FAMILY ctx=$MOSAIC_CONTEXT mmap=0" >> "$MOSAIC_LOG"
    return 1
}

cmd_stop() {
    load_env
    if [[ -f "$MOSAIC_PID_FILE" ]]; then
        local pid
        pid="$(cat "$MOSAIC_PID_FILE" 2>/dev/null || true)"
        if [[ -n "${pid:-}" ]] && kill -0 "$pid" 2>/dev/null; then
            kill "$pid" || true
            ok "Stopped Mosaic pid $pid"
        fi
        rm -f "$MOSAIC_PID_FILE"
    else
        info "No Mosaic pid file (server was not launched by mosaic.sh)."
    fi
}

cmd_status() {
    load_env
    local url
    url="$(mosaic_base_url)"
    echo -n "mosaic ($url): "
    if curl -sf "$url/health" &>/dev/null; then
        echo "healthy"
        curl -sf "$url/v1/models" 2>/dev/null || true
        echo
    else
        echo "not running (scaffold milestone; see docs/MIMO.md)"
    fi
    echo "family=$MOSAIC_FAMILY ctx=$MOSAIC_CONTEXT mmap=0 pack=$MOSAIC_PACK_DIR"
}

cmd_logs() {
    local n=80
    if [[ "${1:-}" == "--tail" ]]; then n="${2:-80}"; fi
    if [[ -f "$MOSAIC_LOG" ]]; then
        tail -n "$n" "$MOSAIC_LOG"
    else
        info "No Mosaic log yet at $MOSAIC_LOG"
    fi
}

usage() {
    echo "Usage: mosaic.sh {check|install|start|stop|status|logs}"
}

cmd="${1:-}"
shift || true
case "$cmd" in
    check)   cmd_check "$@" ;;
    install) cmd_install "$@" ;;
    start)   cmd_start "$@" ;;
    stop)    cmd_stop "$@" ;;
    status)  cmd_status "$@" ;;
    logs)    cmd_logs "$@" ;;
    *)       usage; exit 2 ;;
esac
