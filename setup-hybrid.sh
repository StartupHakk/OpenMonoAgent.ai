#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMonoAgent.ai — Hybrid MoE + TurboQuant Setup
#
# Configures OpenMono to run Qwen3.6-35B-A3B with:
#   - Non-expert layers on GPU (attention, embeddings)
#   - Inactive MoE experts in system RAM (--n-cpu-moe)
#   - KV cache compressed with TurboQuant (turbo3/turbo4)
#   - Active experts computed on GPU
#
# Prerequisites:
#   - OpenMonoAgent.ai installed (or at least cloned)
#   - Docker with NVIDIA Container Toolkit
#   - 8 GB+ VRAM, 32 GB+ RAM
#
# Usage:
#   ./setup-hybrid.sh                # Interactive (prompts for choices)
#   ./setup-hybrid.sh --auto         # Non-interactive, default choices
#   ./setup-hybrid.sh --model-only   # Just download the model, skip build
#   ./setup-hybrid.sh --build-only   # Just build the Docker image
#   ./setup-hybrid.sh --apply-only   # Just apply the compose override + config
# ──────────────────────────────────────────────────────────────────────────────

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODEL_DIR="$SCRIPT_DIR/models"
DOCKER_DIR="$SCRIPT_DIR/docker"

# ── Model config ──────────────────────────────────────────────────────────────
MODEL_NAME="Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
MODEL_ALIAS="qwen3.6-35b-a3b"
MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"

# MTP variant (speculative decoding, ~1.2x speedup on MoE):
# MODEL_NAME="Qwen3.6-35B-A3B-MTP-UD-Q4_K_XL.gguf"
# MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/main/Qwen3.6-35B-A3B-MTP-UD-Q4_K_XL.gguf"

# TurboQuant+ repo
TURBOQUANT_REPO="https://github.com/TheTom/llama-cpp-turboquant.git"
TURBOQUANT_BRANCH="feature/turboquant-kv-cache"
TURBOQUANT_IMAGE="openmono-llama-turboquant"

# ── Colors ────────────────────────────────────────────────────────────────────
BOLD='\033[1m'
BLUE='\033[38;2;163;255;102m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m'
DIM='\033[2m'

info()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }
die()   { err "$*"; exit 1; }
detail(){ echo -e "  ${DIM}$*${NC}"; }

# ── Parse args ────────────────────────────────────────────────────────────────
AUTO=false
MODEL_ONLY=false
BUILD_ONLY=false
APPLY_ONLY=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --auto)     AUTO=true; shift ;;
        --model-only)  MODEL_ONLY=true; shift ;;
        --build-only)  BUILD_ONLY=true; shift ;;
        --apply-only)  APPLY_ONLY=true; shift ;;
        --help|-h)  echo "Usage: $0 [--auto] [--model-only|--build-only|--apply-only]"; exit 0 ;;
        *)          die "Unknown option: $1" ;;
    esac
done

# If no specific phase flags, do all
DO_ALL=false
if ! $MODEL_ONLY && ! $BUILD_ONLY && ! $APPLY_ONLY; then
    DO_ALL=true
fi

# ── Preflight checks ──────────────────────────────────────────────────────────

check_docker() {
    if ! docker info &>/dev/null 2>&1; then
        if id -nG 2>/dev/null | grep -qw docker; then
            err "Docker group active but daemon not reachable."
            err "Start Docker: sudo systemctl start docker"
        else
            err "User '$USER' not in docker group."
            err "Run: sudo usermod -aG docker \"$USER\" && newgrp docker"
        fi
        return 1
    fi

    if ! docker info 2>/dev/null | grep -qi "nvidia"; then
        warn "NVIDIA Container Toolkit not detected in Docker."
        warn "GPU acceleration will not work."
        warn "Install: sudo apt install nvidia-container-toolkit && sudo systemctl restart docker"
        return 1
    fi
}

if $DO_ALL || $BUILD_ONLY || $APPLY_ONLY; then
    check_docker || warn "Proceeding without Docker verification"
fi

# ── Phase 1: Download model ───────────────────────────────────────────────────

download_model() {
    echo ""
    echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BOLD}${CYAN}║  Phase 1: Download MoE Model                           ║${NC}"
    echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════╝${NC}"
    echo ""

    mkdir -p "$MODEL_DIR"
    local model_file="$MODEL_DIR/$MODEL_NAME"

    if [ -f "$model_file" ] && [ "$(stat -c%s "$model_file" 2>/dev/null || echo 0)" -gt 1073741824 ]; then
        local size="$(du -h "$model_file" | cut -f1)"
        ok "Model already present ($size)"
        if ! $AUTO; then
            echo ""
            read -rp "Re-download? [y/N] " confirm
            if [[ ! "$confirm" =~ ^[Yy]$ ]]; then
                return 0
            fi
        else
            return 0
        fi
    fi

    info "Model: $MODEL_NAME (~22.4 GB)"
    info "Source: $MODEL_URL"
    info "Target: $model_file"
    echo ""

    # Probe URL first
    detail "Probing URL..."
    if ! curl -sIL --fail --max-time 15 "$MODEL_URL" >/dev/null 2>&1; then
        err "Cannot reach HuggingFace URL. Check network connectivity."
        return 1
    fi

    info "Downloading (this may take a while)..."
    if ! curl -L --fail --progress-bar -o "$model_file" "$MODEL_URL"; then
        rm -f "$model_file"
        die "Download failed."
    fi

    local size_bytes
    size_bytes="$(stat -c%s "$model_file" 2>/dev/null || echo 0)"
    if [ "$size_bytes" -lt 1073741824 ]; then
        rm -f "$model_file"
        die "Downloaded file suspiciously small ($size_bytes bytes)."
    fi

    ok "Downloaded: $(du -h "$model_file" | cut -f1)"
}

# ── Phase 2: Build TurboQuant+ Docker image ───────────────────────────────────

build_image() {
    echo ""
    echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BOLD}${CYAN}║  Phase 2: Build TurboQuant+ Docker Image               ║${NC}"
    echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════╝${NC}"
    echo ""

    # Check if image already exists
    if docker image inspect "$TURBOQUANT_IMAGE" &>/dev/null 2>&1; then
        ok "Image '$TURBOQUANT_IMAGE' already exists."
        if ! $AUTO; then
            read -rp "Rebuild? [y/N] " confirm
            if [[ ! "$confirm" =~ ^[Yy]$ ]]; then
                ok "Using existing image."
                return 0
            fi
        else
            detail "Skipping build (use --build-only to force rebuild)."
            if ! $BUILD_ONLY; then
                return 0
            fi
        fi
    fi

    info "Building TurboQuant+ llama.cpp with CUDA..."
    info "Repo: $TURBOQUANT_REPO (branch: $TURBOQUANT_BRANCH)"
    info "Image: $TURBOQUANT_IMAGE"
    echo ""
    detail "This compiles CUDA kernels and may take 10-30 minutes."
    echo ""

    if ! $AUTO; then
        read -rp "Proceed with build? [Y/n] " confirm
        if [[ "$confirm" =~ ^[Nn]$ ]]; then
            warn "Build skipped."
            return 0
        fi
    fi

    # Build using the Dockerfile
    if ! docker build \
        -f "$DOCKER_DIR/Dockerfile.turboquant" \
        -t "$TURBOQUANT_IMAGE" \
        "$SCRIPT_DIR" 2>&1; then
        die "Docker build failed."
    fi

    ok "Image built: $TURBOQUANT_IMAGE"
    detail "Tag for GitHub Container Registry:"
    detail "  docker tag $TURBOQUANT_IMAGE ghcr.io/shrekdino/openmono/llama-turboquant:latest"
}

# ── Phase 3: Apply override + config ──────────────────────────────────────────

apply_config() {
    echo ""
    echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BOLD}${CYAN}║  Phase 3: Apply Docker Compose Override + Config       ║${NC}"
    echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════╝${NC}"
    echo ""

    # ── Ask for KV cache type ──────────────────────────────────────────────
    local cache_k="turbo3"
    local cache_v="turbo3"
    local ctx_size="196608"

    if ! $AUTO; then
        echo -e "${BOLD}KV Cache Compression${NC}"
        echo "  1) turbo3 + turbo3  (symmetric, ~4.6x compression — default)"
        echo "  2) turbo4 + turbo4  (symmetric, ~3.8x compression — higher quality)"
        echo "  3) q8_0 + turbo3    (asymmetric, K at q8_0 quality, V compressed)"
        echo "  4) Custom"
        echo ""
        read -rp "Choice [1]: " kv_choice
        case "$kv_choice" in
            2) cache_k="turbo4"; cache_v="turbo4" ;;
            3) cache_k="q8_0";  cache_v="turbo3" ;;
            *) cache_k="turbo3"; cache_v="turbo3" ;;
        esac

        echo ""
        echo -e "${BOLD}Context Size${NC}"
        echo "  1) 196608 (192K — default)"
        echo "  2) 131072 (128K)"
        echo "  3) 65536  (64K)"
        echo "  4) Custom"
        echo ""
        read -rp "Choice [1]: " ctx_choice
        case "$ctx_choice" in
            2) ctx_size="131072" ;;
            3) ctx_size="65536" ;;
            *) ctx_size="196608" ;;
        esac
    fi

    # ── Write docker-compose.override.yml ──────────────────────────────────
    local override_file="$DOCKER_DIR/docker-compose.override.yml"
    info "Writing $override_file ..."

    cat > "$override_file" <<OVERRIDE
# Hybrid MoE + TurboQuant configuration
# Generated by setup-hybrid.sh — $(date +%Y-%m-%d)
#
# Model: Qwen3.6-35B-A3B (MoE, 35B total / 3B active)
# KV cache: ${cache_k} / ${cache_v}
# Context: ${ctx_size}
# Expert offload: --n-cpu-moe (inactive experts in system RAM)
services:
  llama-server:
    image: ${TURBOQUANT_IMAGE}
    container_name: llama-server
    command: >
      --model /models/\${MODEL_NAME:-${MODEL_NAME}}
      --alias \${MODEL_ALIAS:-${MODEL_ALIAS}}
      --host 0.0.0.0
      --port 7474
      --ctx-size ${ctx_size}
      --threads 8
      --threads-batch 8
      --batch-size 2048
      --ubatch-size 1024
      --flash-attn on
      --n-gpu-layers 99
      --n-cpu-moe
      --cache-type-k ${cache_k}
      --cache-type-v ${cache_v}
      --parallel 1
      --jinja
      --reasoning off
      --metrics
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
    volumes:
      - ../models:/models:ro
    ports:
      - "\${LLAMA_PORT:-7474}:7474"
    restart: unless-stopped
    environment:
      - NVIDIA_VISIBLE_DEVICES=all
      - NVIDIA_DRIVER_CAPABILITIES=compute,utility
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:7474/health"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 60s
OVERRIDE
    ok "Override written: $override_file"

    # ── Write docker/.env ───────────────────────────────────────────────────
    local env_file="$DOCKER_DIR/.env"
    info "Updating $env_file ..."

    # Remove existing MODEL_NAME/MODEL_ALIAS entries
    if [ -f "$env_file" ]; then
        grep -v -E "^MODEL_NAME=|^MODEL_ALIAS=" "$env_file" > "${env_file}.tmp" || true
        mv "${env_file}.tmp" "$env_file"
    fi

    printf "MODEL_NAME=%s\nMODEL_ALIAS=%s\n" "$MODEL_NAME" "$MODEL_ALIAS" >> "$env_file"
    ok "Model config written to $env_file"

    # ── Write OpenMono settings.json ───────────────────────────────────────
    local settings_dir="${HOME}/.openmono"
    local settings_file="$settings_dir/settings.json"

    info "Configuring OpenMono settings..."
    mkdir -p "$settings_dir"

    # Merge with existing settings if present
    local temp_file
    temp_file="$(mktemp)"

    # Write base settings with llm config
    cat > "$temp_file" <<SETTINGS
{
  "llm": {
    "endpoint": "http://localhost:7474",
    "model": "${MODEL_ALIAS}",
    "context_size": ${ctx_size},
    "max_output_tokens": 16384,
    "temperature": 0.7,
    "top_p": 0.8,
    "top_k": 20,
    "presence_penalty": 1.5,
    "min_p": 0.0,
    "repetition_penalty": 1.0
  },
  "permissions": {
    "tools": {
      "Bash": {
        "allow": ["git *", "dotnet *", "ls *", "cat *", "curl *"],
        "deny": ["rm -rf /", "sudo *"],
        "ask": ["sudo *", "systemctl *", "docker *"]
      }
    }
  },
  "auto_detect_code_graph": true,
  "verbose": false,
  "data_directory": "~/.openmono"
}
SETTINGS

    # Merge with existing if present
    if [ -f "$settings_file" ]; then
        info "Existing settings.json found — merging llm section"
        # Use Python for proper JSON merge if available, otherwise overwrite
        if command -v python3 &>/dev/null; then
            python3 -c "
import json, sys
existing = json.load(open('$settings_file'))
update = json.load(open('$temp_file'))
existing['llm'] = update['llm']
# Ensure permissions section exists
if 'permissions' not in existing:
    existing['permissions'] = update['permissions']
json.dump(existing, open('$settings_file', 'w'), indent=2)
print('Merged successfully')
" 2>&1 || cp "$temp_file" "$settings_file"
        else
            cp "$temp_file" "$settings_file"
        fi
    else
        cp "$temp_file" "$settings_file"
    fi
    rm -f "$temp_file"
    ok "Settings written: $settings_file"

    echo ""
    echo -e "${BOLD}${GREEN}✓ Hybrid MoE + TurboQuant configuration applied.${NC}"
    echo ""
    detail "To start the server:"
    detail "  cd $DOCKER_DIR && docker compose up -d llama-server"
    detail ""
    detail "To verify:"
    detail "  curl http://localhost:7474/health"
    detail ""
    detail "To run OpenMono agent:"
    detail "  openmono agent"
    echo ""
}

# ── VRAM budget estimate ─────────────────────────────────────────────────────

estimate_vram() {
    echo ""
    echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BOLD}${CYAN}║  VRAM / RAM Budget Estimate                            ║${NC}"
    echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════╝${NC}"
    echo ""

    # Try to get actual VRAM
    local vram_total="?"
    if command -v nvidia-smi &>/dev/null; then
        vram_total="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | awk 'NR==1{print $1}')"
        vram_total="$(( (vram_total + 512) / 1024 )) GB"
    fi

    echo "  ${BOLD}Component${NC}                    ${BOLD}Location${NC}      ${BOLD}Estimate${NC}"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Attention weights (10 layers)      VRAM         ~2.5 GB"
    echo "  Embeddings, norms, LM head         VRAM         ~1.5 GB"
    echo "  Shared MoE expert                  VRAM         ~50 MB"
    echo "  KV cache @192K, turbo3 (~3.5 bpv) VRAM         ~0.8 GB"
    echo "  Activation buffers + overhead      VRAM         ~1-2 GB"
    echo "  ─────────────────────────────────────────────────────────────"
    echo -e "  ${BOLD}Total VRAM needed${NC}                          ${BOLD}~5.8-6.8 GB${NC}"
    echo -e "  ${BOLD}Available VRAM${NC}                            ${BOLD}${vram_total}${NC}"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Routed experts (256 × ~70 MB)      System RAM     ~18 GB"
    echo ""
}

# ── Main ─────────────────────────────────────────────────────────────────────

echo ""
echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BOLD}${CYAN}║                                                    ║${NC}"
echo -e "${BOLD}${CYAN}║     OpenMono  Hybrid MoE + TurboQuant Setup       ║${NC}"
echo -e "${BOLD}${CYAN}║                                                    ║${NC}"
echo -e "${BOLD}${CYAN}║  Qwen3.6-35B-A3B | --n-cpu-moe | turbo3 KV cache  ║${NC}"
echo -e "${BOLD}${CYAN}║                                                    ║${NC}"
echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Show VRAM budget
estimate_vram

if ! $AUTO; then
    echo ""
    read -rp "Continue with hybrid setup? [Y/n] " confirm
    if [[ "$confirm" =~ ^[Nn]$ ]]; then
        die "Aborted."
    fi
fi

if $DO_ALL || $MODEL_ONLY; then
    download_model
fi

if $DO_ALL || $BUILD_ONLY; then
    build_image
fi

if $DO_ALL || $APPLY_ONLY; then
    apply_config
fi

echo ""
echo -e "${BOLD}${GREEN}╔══════════════════════════════════════════════════════════╗${NC}"
echo -e "${BOLD}${GREEN}║  Setup complete.                                       ║${NC}"
echo -e "${BOLD}${GREEN}╚══════════════════════════════════════════════════════════╝${NC}"
echo ""
detail "Quick start:"
detail "  1. cd $DOCKER_DIR"
detail "  2. docker compose up -d llama-server"
detail "  3. openmono agent"
echo ""
detail "Monitor:"
detail "  docker compose logs -f llama-server"
detail "  nvidia-smi --query-gpu=memory.used,utilization.gpu --format=csv -l 1"
echo ""
