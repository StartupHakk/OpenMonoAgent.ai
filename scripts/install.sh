#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai Installer
#
# Builds Docker images, downloads the model, and starts the llama-server
# daemon. Assumes prerequisites (Docker, curl, etc.) are already installed by
# scripts/install_prereqs.sh.
#
# Options (via env or openmono CLI flags):
#   OPENMONO_ROLE         Install role: full (default), inference, or agent
#                         full      = both sides on one machine (today's behaviour)
#                         inference = GPU box only: model + llama-server, no agent tooling
#                         agent     = laptop only: agent + code-review-graph, no model
#   OPENMONO_GPU=1        Force GPU mode (writes GPU docker-compose override)
#   OPENMONO_CPU=1        Force CPU mode (removes any GPU override)
#   OPENMONO_VERBOSE=1    Show detailed command output
#   LLAMA_PORT=7474       llama-server host port (default 7474)
#
# Dev/testing only (not user-facing):
#   OPENMONO_MODEL_MIRROR=http://192.168.x.x:8080
#                         Override the HuggingFace base URL for model downloads.
#                         The path and filename are preserved; only the host is
#                         swapped.  Leave unset for normal HuggingFace downloads.
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$(dirname "$SCRIPT_DIR")"  # repo root (parent of scripts/)
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

# ── Context size presets (tokens) — edit here to change globally ─────────────
CTX_24G=196608       # GPU 24GB+  q8_0 KV  (~8GB KV + 15GB weights ≈ 23GB)
CTX_16G=180224       # GPU 16GB   q4_0 KV  (~3.7GB KV + 12GB weights ≈ 16GB)
CTX_12G=196608       # GPU 12GB   q4_0 KV  (9B weights ~5GB, fits)
CTX_CPU=196608       # CPU/Vulkan q8_0 KV
CTX_VISION=172032    # 24GB+ tier + mmproj — 723 MiB free at rest; vision encoder needs ~474 MiB burst
CTX_VISION_16G=98304  # 16GB tier + mmproj — ~1GB less KV than 128k, gives vision encoder burst headroom

# MODEL_ACCURACY, MODEL_ALIAS, and _MODEL_LABEL.
select_model() {
    case "${1:-0}" in
        24) MODEL_NAME="Qwen3.6-27B-Q4_K_M.gguf"
            MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/Qwen3.6-27B-Q4_K_M.gguf"
            MODEL_ACCURACY="full"
            MODEL_MMPROJ="mmproj-qwen3.6-27B-F16.gguf"
            MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/mmproj-F16.gguf"
            _MODEL_LABEL="Qwen3.6-27B-Q4_K_M (~15GB) [GPU 24GB+ — full accuracy]" ;;
        16) MODEL_NAME="Qwen3.6-27B-UD-IQ3_XXS.gguf"
            MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/Qwen3.6-27B-UD-IQ3_XXS.gguf"
            MODEL_ACCURACY="lower"
            MODEL_MMPROJ="mmproj-qwen3.6-27B-F16.gguf"
            MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/mmproj-F16.gguf"
            _MODEL_LABEL="Qwen3.6-27B-UD-IQ3_XXS (~12GB) [GPU 16GB — lower accuracy]" ;;
        12) MODEL_NAME="Qwen3.5-9B-Q4_K_M.gguf"
            MODEL_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf"
            MODEL_ACCURACY="lower"
            MODEL_MMPROJ="mmproj-qwen3.5-9B-F16.gguf"
            MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/mmproj-F16.gguf"
            _MODEL_LABEL="Qwen3.5-9B-Q4_K_M (~5GB) [GPU 12GB — lower accuracy]" ;;
        *)  MODEL_NAME="Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
            MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
            MODEL_ACCURACY="standard"
            MODEL_MMPROJ="mmproj-qwen3.6-35B-A3B-F16.gguf"
            MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/mmproj-F16.gguf"
            _MODEL_LABEL="Qwen3.6-35B-A3B (~17.6GB) [CPU]" ;;
    esac
    MODEL_ALIAS="${MODEL_NAME%.gguf}"
}

# ── Auto-recover from fresh `usermod -aG docker` ──────────────────────────────
# Common footgun: install_prereqs.sh just ran `usermod -aG docker $USER`, but
# the *current* shell's supplementary group list was captured at login and
# won't see the new membership until the next shell starts. If we detect:
#   (a) docker is installed,
#   (b) we can't `docker info` without sudo,
#   (c) but the user IS in the docker group at the system level,
# then re-exec ourselves via `sg docker` so the group IS active in the
# subshell. Silent if already fine.
if command -v docker &>/dev/null && ! docker info &>/dev/null 2>&1; then
    if id -nG 2>/dev/null | grep -qw docker; then
        if command -v sg &>/dev/null && sg docker -c "docker info" &>/dev/null 2>&1; then
            info "Re-launching with docker group active (no manual 'newgrp' needed)..."
            exec sg docker -- bash "$0" "$@"
        else
            err "Docker group membership exists but sg activation failed."
            err "Run ONE of the following to activate the docker group:"
            err "  1. newgrp docker"
            err "  2. exec su -l \$USER"
            err "  3. Log out and back in"
            err "Then resume installation with:  $INSTALL_DIR/openmono setup"
            exit 1
        fi
    fi
fi

# Source the shared env file written by install_prereqs.sh (GPU_MODE, AMD_IGPU_MODE, etc.)
# shellcheck source=/dev/null
if [[ -n "${OPENMONO_ENV_FILE:-}" ]] && [[ -f "$OPENMONO_ENV_FILE" ]]; then
    source "$OPENMONO_ENV_FILE"
fi

# Verify reboot if AMD iGPU optimizations were applied
if [[ "${AMD_IGPU_MODE:-0}" = "1" ]]; then
    if ! grep -q "amdgpu.gttsize=28672" /proc/cmdline 2>/dev/null; then
        err "AMD iGPU optimizations have been applied but system has not been rebooted yet."
        err ""
        err "The GRUB kernel parameters and system tuning will not take effect until you reboot."
        err "This will result in OOM errors when starting the llama-server."
        err ""
        err "Please reboot now:"
        err "  sudo reboot"
        err ""
        err "After reboot, run setup again:"
        err "  openmono setup"
        exit 1
    fi
fi

# Role selector — drives which of the 8 install steps actually run.
# If the caller (openmono setup) already exported OPENMONO_ROLE, use it.
# Otherwise prompt — this handles the direct-run path where openmono CLI isn't available yet.
role_prompt

case "$OPENMONO_ROLE" in
    full|inference|agent) ;;
    *) echo "ERROR: Invalid OPENMONO_ROLE='$OPENMONO_ROLE' (expected: full, inference, agent)" >&2; exit 1 ;;
esac

# Step counts vary by role. We keep numbering stable per-role rather than
# printing "skipped" lines — cleaner UX.
case "$OPENMONO_ROLE" in
    full)      TOTAL_STEPS=8 ;;
    inference) TOTAL_STEPS=7 ;;  # skip step 5 (code-review-graph)
    agent)     TOTAL_STEPS=5 ;;  # skip steps 4, 6, 8 (model, GPU, start llama)
esac

banner "OpenMono.ai Installer (role: $OPENMONO_ROLE)"

# Step counter that matches TOTAL_STEPS for this role — lets us skip steps
# without confusing the user with gaps like "Step 6/8".
CURRENT_STEP=0
next_step() {
    CURRENT_STEP=$((CURRENT_STEP + 1))
    step $CURRENT_STEP $TOTAL_STEPS "$1"
}

# ── Prerequisite Check ────────────────────────────────────────────────────────
# Verify that install_prereqs.sh has been run successfully before proceeding.

check_prerequisites() {
    local missing=()
    local warnings=()

    # Required commands
    command -v docker &>/dev/null || missing+=("docker")
    command -v git &>/dev/null || missing+=("git")
    command -v curl &>/dev/null || missing+=("curl")
    command -v cmake &>/dev/null || missing+=("cmake")

    # Docker Compose (plugin or standalone)
    if ! docker compose version &>/dev/null 2>&1 && ! docker-compose version &>/dev/null 2>&1; then
        missing+=("docker-compose")
    fi

    # Check if user can run docker without sudo
    if command -v docker &>/dev/null; then
        if ! docker info &>/dev/null 2>&1; then
            if id -nG 2>/dev/null | grep -qw docker; then
                warnings+=("Docker group membership exists but not active in current shell. Run: newgrp docker")
            else
                warnings+=("User '$USER' is not in the docker group. Run: sudo usermod -aG docker \$USER && newgrp docker")
            fi
        fi
    fi

    # .NET SDK (optional but recommended)
    if ! command -v dotnet &>/dev/null; then
        warnings+=(".NET SDK not installed (optional, but recommended)")
    fi

    # Check for NVIDIA requirements if GPU is present
    HAS_NVIDIA_HW=false
    if command -v lspci &>/dev/null && lspci 2>/dev/null | grep -qi 'nvidia'; then
        HAS_NVIDIA_HW=true
    elif grep -qi "0x10de" /sys/bus/pci/devices/*/vendor 2>/dev/null; then
        HAS_NVIDIA_HW=true
    fi

    if [ "$HAS_NVIDIA_HW" = true ]; then
        if ! command -v nvidia-smi &>/dev/null; then
            warnings+=("NVIDIA GPU detected but drivers not installed")
        fi
        if ! dpkg -s nvidia-container-toolkit &>/dev/null 2>&1; then
            warnings+=("nvidia-container-toolkit not installed (required for GPU Docker)")
        fi
    fi

    # Report results
    if [ ${#missing[@]} -gt 0 ]; then
        err "Missing required prerequisites:"
        for pkg in "${missing[@]}"; do
            printf "  ${RED}✗${NC}  %s\n" "$pkg"
        done
        echo ""
        err "Please run the prerequisites installer first:"
        err "  ./scripts/install_prereqs.sh"
        echo ""
        die "Cannot continue without required prerequisites."
    fi

    if [ ${#warnings[@]} -gt 0 ]; then
        warn "Prerequisite warnings:"
        for w in "${warnings[@]}"; do
            printf "  ${YELLOW}⚠${NC}  %s\n" "$w"
        done
        echo ""
    fi

    ok "All prerequisites satisfied"
}


info "Checking prerequisites..."
check_prerequisites

# ── Step 1: Resolve install directory (all roles) ────────────────────────────

next_step "Resolving install directory"

if [ -f "$SCRIPT_DIR/../OpenMono.sln" ]; then
    INSTALL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
elif [ -n "${OPENMONO_HOME:-}" ]; then
    INSTALL_DIR="$OPENMONO_HOME"
else
    INSTALL_DIR="$HOME/openmono.ai"
fi

ok "Install directory: $INSTALL_DIR"

# ── Step 2: Check system requirements (all roles) ────────────────────────────

next_step "Checking system requirements"

# GPU_MODE is exported by install_prereqs.sh (1 = GPU, 0 = CPU).
_VRAM_MB=0
_GPU_TIER=0
MODEL_NAME=""
MODEL_ACCURACY=""
MODEL_ALIAS=""
MODEL_MMPROJ=""
MODEL_MMPROJ_URL=""
if [ "$OPENMONO_ROLE" != "agent" ]; then
    if [ "${GPU_MODE:-0}" = "1" ]; then
        if command -v nvidia-smi &>/dev/null; then
            _VRAM_MB=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | awk 'NR==1{print $1}')
            _VRAM_MB=${_VRAM_MB:-0}
            if   [ "$_VRAM_MB" -ge 24000 ]; then _GPU_TIER=24; ok "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — full accuracy tier (24GB+)"
            elif [ "$_VRAM_MB" -ge 16000 ]; then _GPU_TIER=16; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — lower accuracy tier (16GB). For best results use 24GB+ VRAM."
            elif [ "$_VRAM_MB" -ge 12000 ]; then _GPU_TIER=12; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — lower accuracy tier (12GB). For best results use 24GB+ VRAM."
            else
                warn "GPU mode selected but only $(( (_VRAM_MB + 512) / 1024 ))GB VRAM — minimum is 12GB. Falling back to CPU mode."
                GPU_MODE=0
            fi
        else
            warn "GPU mode selected but nvidia-smi not found. Falling back to CPU mode."
            GPU_MODE=0
        fi
    else
        if command -v free &>/dev/null; then
            TOTAL_MEM=$(free -g | awk '/^Mem:/{print $2}')
            if [ "$TOTAL_MEM" -lt 20 ]; then
                warn "Only ${TOTAL_MEM}GB RAM detected — CPU model needs ~20GB. It may be slow or fail to load."
            else
                ok "RAM: ${TOTAL_MEM}GB"
            fi
        fi
    fi
    select_model "$_GPU_TIER"
    ok "Model selected: $MODEL_NAME"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        detail "Vision projector: $MODEL_MMPROJ"
    fi
fi

# Display tool versions (prerequisites already verified above)
detail "docker: $(docker --version 2>/dev/null | head -1)"
detail "git: $(git --version 2>/dev/null)"
detail "curl: $(curl --version 2>/dev/null | head -1)"

if docker compose version &>/dev/null 2>&1; then
    detail "docker compose: $(docker compose version --short 2>/dev/null || echo plugin)"
else
    detail "docker-compose (legacy): $(docker-compose version --short 2>/dev/null || echo legacy)"
fi

PIP_CMD=""
if command -v pip3 &>/dev/null; then
    PIP_CMD="pip3"
elif command -v pip &>/dev/null; then
    PIP_CMD="pip"
fi
[ -z "$PIP_CMD" ] && warn "pip/pip3 not found — optional python deps will be skipped on host"

ok "System requirements verified"

# ── Step 3: Fetch repo if missing (all roles) ────────────────────────────────

next_step "Verifying repository"

if [ ! -f "$INSTALL_DIR/OpenMono.sln" ]; then
    info "Cloning OpenMono.ai repository to $INSTALL_DIR..."
    run git clone https://github.com/StartupHakk/OpenMonoAgent.ai.git "$INSTALL_DIR" \
        || die "git clone failed"
    ok "Repository cloned"
else
    ok "Repository present"
fi

cd "$INSTALL_DIR"

# ── Step 4: Download model (inference + full only) ───────────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    MODEL_DIR="$INSTALL_DIR/models"

    next_step "Downloading $_MODEL_LABEL"

    # Dev override: fetch from local mirror at http://<host>/models/<filename>
    if [ -n "${OPENMONO_MODEL_MIRROR:-}" ]; then
        MODEL_URL="${OPENMONO_MODEL_MIRROR%/}/models/${MODEL_NAME}"
        detail "Model mirror active: $MODEL_URL"
    fi

    MODEL_FILE="$MODEL_DIR/$MODEL_NAME"
    MODEL_MIN_BYTES=$((1024 * 1024 * 1024))  # 1 GB sanity check (real file ~18.5 GB)

    mkdir -p "$MODEL_DIR"

    model_size() { stat -c%s "$1" 2>/dev/null || echo 0; }

    if [ -f "$MODEL_FILE" ] && [ "$(model_size "$MODEL_FILE")" -gt "$MODEL_MIN_BYTES" ]; then
        ok "Model already present ($(du -h "$MODEL_FILE" | cut -f1))"
    else
        if [ -f "$MODEL_FILE" ]; then
            warn "Existing model file looks incomplete ($(du -h "$MODEL_FILE" | cut -f1)) — removing"
            rm -f "$MODEL_FILE"
        fi

        info "Source: $MODEL_URL"
        info "Target: $MODEL_FILE"
        info "This will take a while depending on network speed."

        # Probe URL first so failures surface fast
        detail "Probing URL..."
        if ! curl -sIL --fail --max-time 15 "$MODEL_URL" >/dev/null 2>&1; then
            err "HuggingFace URL is not reachable"
            err "URL: $MODEL_URL"
            err "Possible causes:"
            err "  - Network/firewall blocking huggingface.co"
            err "  - Model gated behind auth (unlikely for this repo)"
            die "Cannot reach model URL"
        fi

        # Progress-bar always on, even in quiet mode — download is long
        if ! run_live curl -L --fail --progress-bar -o "$MODEL_FILE" "$MODEL_URL"; then
            rm -f "$MODEL_FILE"
            die "Model download failed"
        fi

        # Sanity-check size
        SIZE_BYTES=$(model_size "$MODEL_FILE")
        if [ "$SIZE_BYTES" -lt "$MODEL_MIN_BYTES" ]; then
            rm -f "$MODEL_FILE"
            die "Downloaded file is suspiciously small ($SIZE_BYTES bytes). Likely an HTTP error page."
        fi

        ok "Model downloaded ($(du -h "$MODEL_FILE" | cut -f1))"
    fi

    # ── mmproj (vision projector) ────────────────────────────────────────────
    if [ -z "${MODEL_MMPROJ:-}" ]; then
        info "No mmproj configured for this model — vision will be disabled"
    else
        MMPROJ_FILE="$MODEL_DIR/$MODEL_MMPROJ"
        MMPROJ_MIN_BYTES=$((100 * 1024 * 1024))  # 100 MB sanity check (real file ~900 MB)
        if [ -f "$MMPROJ_FILE" ] && [ "$(model_size "$MMPROJ_FILE")" -gt "$MMPROJ_MIN_BYTES" ]; then
            ok "mmproj already present ($(du -h "$MMPROJ_FILE" | cut -f1))"
        else
            if [ -f "$MMPROJ_FILE" ]; then
                warn "Existing mmproj looks incomplete — removing"
                rm -f "$MMPROJ_FILE"
            fi
            info "Downloading mmproj: $MODEL_MMPROJ (~900 MB)"
            if ! run_live curl -L --fail --progress-bar -o "$MMPROJ_FILE" "$MODEL_MMPROJ_URL"; then
                rm -f "$MMPROJ_FILE"
                warn "mmproj download failed — vision will be unavailable"
                MODEL_MMPROJ=""
            else
                ok "mmproj downloaded ($(du -h "$MMPROJ_FILE" | cut -f1))"
            fi
        fi
    fi
fi

# ── Step 5: code-review-graph (agent + full only) ────────────────────────────

if [ "$OPENMONO_ROLE" != "inference" ]; then
    next_step "Setting up code-review-graph"

    if command -v code-review-graph &>/dev/null; then
        ok "code-review-graph already installed"
    elif [ -n "$PIP_CMD" ]; then
        info "Installing code-review-graph via $PIP_CMD..."
        if run $PIP_CMD install --user code-review-graph; then
            ok "code-review-graph installed"
        elif run $PIP_CMD install --user --break-system-packages code-review-graph; then
            ok "code-review-graph installed (--break-system-packages)"
        else
            warn "Could not install code-review-graph via pip — Docker image includes it"
        fi
    else
        warn "Skipping host install of code-review-graph (no pip). Docker image includes it."
    fi

    REF_DIR="$INSTALL_DIR/ref"
    GRAPH_DB_DIR="$HOME/.openmono/graph-db"
    if [ -d "$REF_DIR" ] && [ -n "$(ls -A "$REF_DIR" 2>/dev/null)" ]; then
        info "Building code graph from ref/..."
        mkdir -p "$GRAPH_DB_DIR"
        GRAPH_CMD="code-review-graph"
        command -v code-review-graph &>/dev/null || GRAPH_CMD="$HOME/.local/bin/code-review-graph"
        if run "$GRAPH_CMD" build --repo "$REF_DIR"; then
            ok "Code graph built"
        else
            warn "Graph build had warnings (see log)"
        fi
    else
        info "ref/ is empty — skipping graph build"
        info "Later: put code under ref/ and run: openmono graph"
    fi

    # graphify — semantic knowledge graph (complements code-review-graph)
    if command -v graphify &>/dev/null; then
        ok "graphify already installed"
    elif [ -n "$PIP_CMD" ]; then
        info "Installing graphify via $PIP_CMD..."
        if run $PIP_CMD install --user graphifyy; then
            ok "graphify installed"
        elif run $PIP_CMD install --user --break-system-packages graphifyy; then
            ok "graphify installed (--break-system-packages)"
        else
            warn "Could not install graphify via pip — install manually: pip install graphifyy && graphify install"
        fi
    else
        warn "Skipping host install of graphify (no pip). Install manually: pip install graphifyy && graphify install"
    fi
fi

# ── Step 6: Configure GPU/CPU mode (inference + full only) ───────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Configuring GPU / CPU mode"

    if [ "${GPU_MODE:-0}" = "1" ]; then
        info "GPU mode (selected during prerequisites)"
    elif [ "${AMD_IGPU_MODE:-0}" = "1" ]; then
        info "AMD iGPU mode (Vulkan)"
    else
        info "CPU mode"
    fi

    OVERRIDE_FILE="$INSTALL_DIR/docker/docker-compose.override.yml"
    
    if [ "${GPU_MODE:-0}" = "1" ]; then
        # 24GB tier: q8 kv cache (high quality); 16GB/12GB tiers: q4 kv cache (saves VRAM)
        if [ "$_GPU_TIER" -ge 24 ]; then
            _KV_K="q8_0"; _KV_V="q8_0"
            _CTX=$CTX_24G
        elif [ "$_GPU_TIER" -ge 16 ]; then
            _KV_K="q4_0"; _KV_V="q4_0"
            _CTX=$CTX_16G
        else
            _KV_K="q4_0"; _KV_V="q4_0"
            _CTX=$CTX_12G
        fi
        # mmproj loads ~1–2 GB into VRAM — pull context back to stay within budget.
        if [ -n "${MODEL_MMPROJ:-}" ]; then
            _CTX_ORIG=$_CTX
            if [ "$_GPU_TIER" -ge 24 ]; then
                _CTX=$CTX_VISION
            else
                _CTX=$CTX_VISION_16G
            fi
            detail "Vision enabled: context reduced from $(( _CTX_ORIG / 1024 ))k → $(( _CTX / 1024 ))k to fit mmproj in VRAM"
        fi
        [ "$MODEL_ACCURACY" = "lower" ] && info "Lower accuracy model selected — q4 kv cache enabled to fit $(( (_VRAM_MB + 512) / 1024 ))GB VRAM"
        # Append mmproj flag inline with --metrics; empty string when no projector configured.
        MMPROJ_OPT="${MODEL_MMPROJ:+--mmproj /models/${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}"
        info "Writing GPU override: $OVERRIDE_FILE"
        cat > "$OVERRIDE_FILE" <<EOF
# GPU configuration (auto-generated by install.sh — ${MODEL_ACCURACY:-full} accuracy)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-cuda
    command: >
      --model /models/\${MODEL_NAME}
      --alias \${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size $_CTX
      --threads 14
      --n-gpu-layers 99
      --flash-attn on
      --cache-type-k $_KV_K
      --cache-type-v $_KV_V
      --batch-size 2048
      --ubatch-size 1024
      --parallel 1
      --jinja
      --reasoning off
      --metrics ${MMPROJ_OPT}
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
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
EOF
    ok "GPU override written"
    printf "     ${DIM}model: $MODEL_NAME  ·  ctx: $(( _CTX / 1024 ))k  ·  kv: ${_KV_K}  ·  layers: 99  ·  accuracy: ${MODEL_ACCURACY:-full}  ·  vision: ${MODEL_MMPROJ:-disabled}${NC}\n"
    printf "     ${DIM}config: $OVERRIDE_FILE  (restart: docker compose up -d llama-server)${NC}\n"

    printf "\n  ${BOLD}${CYAN}Tuning knobs${NC}\n"
    printf "     ${DIM}--ctx-size N      halve to free ~half the KV-cache VRAM${NC}\n"
    printf "     ${DIM}                  presets: 32k=32768  64k=65536  128k=131072  192k=196608${NC}\n"
    printf "     ${DIM}--cache-type-k/v  f16 (best) → q8_0 → q5_1 → q4_1 → q4_0 (least VRAM)${NC}\n"
    printf "     ${DIM}--n-gpu-layers    99=all on GPU; lower to spill layers to CPU RAM${NC}\n"
    printf "     ${DIM}--parallel N      N concurrent slots, each costs ctx-size of KV cache${NC}\n"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
    printf "\n  ${BOLD}${CYAN}Vision (enabled by default)${NC}\n"
    printf "     ${DIM}Vision encoder (mmproj) is loaded alongside the model${NC}\n"
    printf "     ${DIM}  · GPU: uses ~1–2 GB extra VRAM — context reduced $(( _CTX_ORIG / 1024 ))k → $(( _CTX / 1024 ))k to compensate${NC}\n"
    printf "     ${DIM}  · CPU: uses ~1–2 GB extra RAM — same context reduction applies${NC}\n"
    printf "     ${DIM}Use: @image.png or @screenshot.png in chat, or ask the agent to read any image file${NC}\n"
    printf "\n     ${DIM}To disable vision and recover $(( (_CTX_ORIG - _CTX) / 1024 ))k extra context:${NC}\n"
    printf "     ${DIM}  1.  docker/.env → MODEL_MMPROJ=  (clear the value)${NC}\n"
    printf "     ${DIM}  2.  docker/.env → CTX_SIZE=%s  (restore full context)${NC}\n" "$_CTX_ORIG"
    printf "     ${DIM}  3.  docker compose up -d llama-server${NC}\n"
    fi

    printf "\n  ${BOLD}${CYAN}Swapping models${NC}\n"
    printf "     ${DIM}Any GGUF works — different family or a different quant of this model${NC}\n"
    printf "     ${DIM}1.  Copy .gguf into $INSTALL_DIR/models/${NC}\n"
    printf "     ${DIM}2.  docker/.env → MODEL_NAME=file.gguf  MODEL_ALIAS=file${NC}\n"
    printf "     ${DIM}3.  docker compose up -d llama-server${NC}\n"
    printf "     \n"
    printf "     ${DIM}Quants:  Q6_K > Q5_K_M > Q4_K_M > Q3_K_M  (quality vs VRAM)${NC}\n"
    printf "     ${DIM}IQ:      IQ3_XXS / IQ4_XS match one tier higher quality at lower size${NC}\n"
    printf "     ${DIM}MoE:     A3B / A22B suffix — only active params computed, runs faster${NC}\n"
    printf "     ${DIM}Tier or GPU↔CPU change: re-run openmono setup${NC}\n"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
    printf "     ${DIM}Vision: if swapping to a different model family, update MODEL_MMPROJ= too${NC}\n"
    printf "     ${DIM}  mmproj must match the model base — search HuggingFace for '<model-name> mmproj gguf'${NC}\n"
    fi
elif [ "${AMD_IGPU_MODE:-0}" = "1" ]; then
    info "Writing AMD iGPU (Vulkan) override: $OVERRIDE_FILE"

    # Context size reduction for mmproj (same as CPU branch)
    _CTX_ORIG=196608
    _CTX=196608
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        _CTX=$CTX_VISION
        detail "Vision enabled: context reduced from $(( _CTX_ORIG / 1024 ))k → $(( CTX_VISION / 1024 ))k to fit mmproj in GTT allocation"
    fi
    MMPROJ_OPT="${MODEL_MMPROJ:+--mmproj /models/${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}"

    cat > "$OVERRIDE_FILE" <<EOF
# AMD Radeon 780M iGPU / Vulkan configuration (auto-generated by install.sh)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-vulkan-b9070
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "44"
      - "109"
    volumes:
      - ~/openmono.ai/models:/models
    ports:
      - "\${LLAMA_PORT:-7474}:\${LLAMA_PORT:-7474}"
    shm_size: "4gb"
    environment:
      - GGML_VULKAN_DEVICE=0
      - RADV_PERFTEST=compute
    command: >
      --model /models/\${MODEL_NAME}
      --alias \${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size $_CTX
      --no-mmap
      --threads 8
      --threads-batch 8
      --batch-size 512
      --ubatch-size 256
      -ngl 99
      --n-gpu-layers 99
      --flash-attn on
      --cache-type-k q8_0
      --cache-type-v q8_0
      --parallel 1
      --jinja
      --reasoning off
      --metrics ${MMPROJ_OPT}
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
EOF
    ok "AMD iGPU override written"
    printf "     ${DIM}model: $MODEL_NAME  ·  ctx: $(( _CTX / 1024 ))k  ·  kv: q8_0  ·  gpu-layers: 99  ·  vision: ${MODEL_MMPROJ:-disabled}${NC}\n"
    printf "     ${DIM}config: $OVERRIDE_FILE  (restart: docker compose up -d llama-server)${NC}\n"

    printf "\n  ${BOLD}${CYAN}Tuning knobs${NC}\n"
    printf "     ${DIM}--ctx-size N      up to 196608 (GTT limits: ~28GB system RAM as VRAM)${NC}\n"
    printf "     ${DIM}--cache-type-k/v  f16 (best) → q8_0 → q5_1 → q4_1 → q4_0 (least RAM)${NC}\n"
    printf "     ${DIM}--no-mmap         prevents kernel double-mapping with GTT allocation${NC}\n"

    printf "\n  ${BOLD}${CYAN}Swapping models${NC}\n"
    printf "     ${DIM}Any GGUF works — different family or a different quant of this model${NC}\n"
    printf "     ${DIM}1.  Copy .gguf into \$INSTALL_DIR/models/${NC}\n"
    printf "     ${DIM}2.  docker/.env → MODEL_NAME=file.gguf  MODEL_ALIAS=file${NC}\n"
    printf "     ${DIM}3.  docker compose up -d llama-server${NC}\n"
    printf "     \n"
    printf "     ${DIM}Quants:  Q6_K > Q5_K_M > Q4_K_M > Q3_K_M  (quality vs RAM)${NC}\n"
    printf "     ${DIM}IQ:      IQ3_XXS / IQ4_XS match one tier higher quality at lower size${NC}\n"
    printf "     ${DIM}MoE:     A3B / A22B suffix — only active params computed, runs faster${NC}\n"
    printf "     ${DIM}Architecture: Radeon 780M + 28GB GTT window (GRUB configured)${NC}\n"

else
    info "Writing CPU override: $OVERRIDE_FILE"
    # Thread count tuned to physical cores (SMT hurts llama.cpp throughput).
    CPU_THREADS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 8)"
    PHYS_CORES="$(lscpu -b -p=Core,Socket 2>/dev/null | grep -v '^#' | sort -u | wc -l)"
    [ "${PHYS_CORES:-0}" -gt 0 ] && CPU_THREADS="$PHYS_CORES"
    _CTX_ORIG=$CTX_CPU
    _CTX=$CTX_CPU
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        _CTX=$CTX_VISION
        detail "Vision enabled: context reduced from $(( _CTX_ORIG / 1024 ))k → $(( CTX_VISION / 1024 ))k to fit mmproj in RAM"
    fi
    MMPROJ_OPT="${MODEL_MMPROJ:+--mmproj /models/${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}"
    cat > "$OVERRIDE_FILE" <<EOF
# CPU-only configuration (auto-generated by install.sh)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server
    command: >
      --model /models/\${MODEL_NAME}
      --alias \${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size $_CTX
      --threads $CPU_THREADS
      --threads-batch $CPU_THREADS
      --batch-size 2048
      --ubatch-size 1024
      --flash-attn on
      --cache-type-k q8_0
      --cache-type-v q8_0
      --parallel 1
      --jinja
      --reasoning off
      --metrics ${MMPROJ_OPT}
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
EOF
    ok "CPU override written"
    printf "     ${DIM}model: $MODEL_NAME  ·  ctx: $(( _CTX / 1024 ))k  ·  kv: q8_0  ·  threads: $CPU_THREADS (physical cores)  ·  vision: ${MODEL_MMPROJ:-disabled}${NC}\n"
    printf "     ${DIM}config: $OVERRIDE_FILE  (restart: docker compose up -d llama-server)${NC}\n"

    printf "\n  ${BOLD}${CYAN}Tuning knobs${NC}\n"
    printf "     ${DIM}--ctx-size N      halve to free ~half the KV-cache RAM, speeds up prompt processing${NC}\n"
    printf "     ${DIM}                  presets: 32k=32768  64k=65536  128k=131072  192k=196608${NC}\n"
    printf "     ${DIM}--cache-type-k/v  f16 (best) → q8_0 → q5_1 → q4_1 → q4_0 (least RAM)${NC}\n"
    printf "     ${DIM}--threads N       physical cores optimal ($CPU_THREADS); SMT/HT hurts llama.cpp throughput${NC}\n"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
    printf "\n  ${BOLD}${CYAN}Vision (enabled by default)${NC}\n"
    printf "     ${DIM}Vision encoder (mmproj) is loaded alongside the model${NC}\n"
    printf "     ${DIM}  · GPU: uses ~1–2 GB extra VRAM — context reduced $(( _CTX_ORIG / 1024 ))k → $(( CTX_VISION / 1024 ))k to compensate${NC}\n"
    printf "     ${DIM}  · CPU: uses ~1–2 GB extra RAM — same context reduction applies${NC}\n"
    printf "     ${DIM}Use: @image.png or @screenshot.png in chat, or ask the agent to read any image file${NC}\n"
    printf "\n     ${DIM}To disable vision and recover $(( (_CTX_ORIG - CTX_VISION) / 1024 ))k extra context:${NC}\n"
    printf "     ${DIM}  1.  docker/.env → MODEL_MMPROJ=  (clear the value)${NC}\n"
    printf "     ${DIM}  2.  docker/.env → CTX_SIZE=%s  (restore full context)${NC}\n" "$_CTX_ORIG"
    printf "     ${DIM}  3.  docker compose up -d llama-server${NC}\n"
    fi

    printf "\n  ${BOLD}${CYAN}Swapping models${NC}\n"
    printf "     ${DIM}Any GGUF works — different family or a different quant of this model${NC}\n"
    printf "     ${DIM}1.  Copy .gguf into $INSTALL_DIR/models/${NC}\n"
    printf "     ${DIM}2.  docker/.env → MODEL_NAME=file.gguf  MODEL_ALIAS=file${NC}\n"
    printf "     ${DIM}3.  docker compose up -d llama-server${NC}\n"
    printf "     \n"
    printf "     ${DIM}Quants:  Q6_K > Q5_K_M > Q4_K_M > Q3_K_M  (quality vs RAM)${NC}\n"
    printf "     ${DIM}IQ:      IQ3_XXS / IQ4_XS match one tier higher quality at lower size${NC}\n"
    printf "     ${DIM}MoE:     A3B / A22B suffix — only active params computed, runs faster${NC}\n"
    printf "     ${DIM}Switch to GPU: re-run openmono setup${NC}\n"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
    printf "     ${DIM}Vision: if swapping to a different model family, update MODEL_MMPROJ= too${NC}\n"
    printf "     ${DIM}  mmproj must match the model base — search HuggingFace for '<model-name> mmproj gguf'${NC}\n"
    fi

    # ── CPU tuning: request the 'performance' power profile ────────────────
    # llama.cpp on CPU is limited by sustained clock speed. The default
    # 'balanced' profile on most laptops/desktops throttles aggressively
    # under sustained load, costing 20-40% throughput. Bump to 'performance'
    # for the duration of this host's life — user can switch back any time.
    if command -v powerprofilesctl &>/dev/null; then
        current_profile="$(powerprofilesctl get 2>/dev/null || echo unknown)"
        if [ "$current_profile" = "performance" ]; then
            ok "Power profile already 'performance'"
        else
            info "Setting power profile to 'performance' (was: $current_profile)"
            if powerprofilesctl set performance 2>/dev/null; then
                ok "Power profile set to 'performance'"
            else
                warn "Could not set power profile automatically. To apply:"
                warn "  sudo powerprofilesctl set performance"
            fi
        fi
    else
        info "powerprofilesctl not found — skipping power-profile tuning."
        info "For best CPU throughput, ensure your system is in performance mode"
        info "(e.g. 'tuned-adm profile throughput-performance' or"
        info " 'cpupower frequency-set -g performance' depending on your distro)."
    fi
fi

DOCKER_ENV_FILE="$INSTALL_DIR/docker/.env"
if [ -f "$DOCKER_ENV_FILE" ]; then
    grep -v -E "^MODEL_NAME=|^MODEL_ALIAS=|^MODEL_MMPROJ=|^OPENMONO_VISION_ENABLED=|^CTX_SIZE=" "$DOCKER_ENV_FILE" > "${DOCKER_ENV_FILE}.tmp" || true
    mv "${DOCKER_ENV_FILE}.tmp" "$DOCKER_ENV_FILE"
fi
printf "MODEL_NAME=%s\nMODEL_ALIAS=%s\nMODEL_MMPROJ=%s\nCTX_SIZE=%s\n" "$MODEL_NAME" "$MODEL_ALIAS" "${MODEL_MMPROJ:-}" "${_CTX:-$CTX_CPU}" >> "$DOCKER_ENV_FILE"
if [ -n "${MODEL_MMPROJ:-}" ]; then
    printf "OPENMONO_VISION_ENABLED=1\n" >> "$DOCKER_ENV_FILE"
    detail "Persisted MODEL_NAME=$MODEL_NAME MODEL_MMPROJ=$MODEL_MMPROJ (vision enabled) to $DOCKER_ENV_FILE"
else
    printf "OPENMONO_VISION_ENABLED=0\n" >> "$DOCKER_ENV_FILE"
    detail "Persisted MODEL_NAME=$MODEL_NAME (vision disabled — no mmproj) to $DOCKER_ENV_FILE"
fi

fi  # End of Step 6 (skipped on agent role)

# ── Step 7: Build Docker images (role-specific) ───────────────────────────────

next_step "Building Docker images"

cd "$INSTALL_DIR/docker"

info "Stopping any running containers..."
run docker compose down || true

# Only build the images this role actually needs.
if [ "$OPENMONO_ROLE" != "agent" ]; then
    info "Building llama-server image..."
    if [ "${GPU_MODE:-0}" = "1" ]; then
        if ! run docker compose build --no-cache llama-server; then
            die "llama-server build failed"
        fi
    else
        if ! run docker compose build llama-server; then
            die "llama-server build failed"
        fi
    fi
fi

if [ "$OPENMONO_ROLE" != "inference" ]; then
    info "Building agent image..."
    if ! run docker compose build agent; then
        die "agent build failed"
    fi
fi

ok "Docker images built"

# ── Step 8: Start llama-server (inference + full only) ───────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Starting llama-server"

    LLAMA_PORT="${LLAMA_PORT:-7474}"

    port_in_use() {
        ss -tlnp 2>/dev/null | grep -q ":${1} " \
        || lsof -i ":${1}" &>/dev/null 2>&1
    }

    if port_in_use "$LLAMA_PORT"; then
        warn "Port ${LLAMA_PORT} is in use"
        if command -v ss &>/dev/null; then
            ss -tlnp 2>/dev/null | grep ":${LLAMA_PORT} " | head -1 | sed 's/^/     /' || true
        fi
        for try in 8081 8082 8083 8084 8085 9080; do
            if ! port_in_use "$try"; then
                LLAMA_PORT="$try"
                info "Using port $LLAMA_PORT instead"
                break
            fi
        done
    fi

    export LLAMA_PORT
    if [ -f "$DOCKER_ENV_FILE" ]; then
        grep -v "^LLAMA_PORT=" "$DOCKER_ENV_FILE" > "${DOCKER_ENV_FILE}.tmp" || true
        mv "${DOCKER_ENV_FILE}.tmp" "$DOCKER_ENV_FILE"
    fi
    echo "LLAMA_PORT=${LLAMA_PORT}" >> "$DOCKER_ENV_FILE"
    detail "Persisted LLAMA_PORT=${LLAMA_PORT} to $DOCKER_ENV_FILE"

    info "Starting daemon on port ${LLAMA_PORT}..."
    if ! run docker compose up -d llama-server; then
        die "Failed to start llama-server (check: docker compose logs llama-server)"
    fi

    info "Waiting for llama-server to become healthy (model load can take 1-2 min)..."
    HEALTHY=false
    for i in $(seq 1 36); do
        if curl -sf "http://localhost:${LLAMA_PORT}/health" &>/dev/null; then
            HEALTHY=true
            break
        fi
        sleep 5
        printf "."
    done
    echo ""

    if [ "$HEALTHY" = true ]; then
        ok "llama-server is healthy on port ${LLAMA_PORT}"
    else
        warn "llama-server did not become healthy within 180s."
        warn "This can be normal on low-RAM systems (model is ~20 GB)."
        warn "Check: openmono logs"
        if [ "$OPENMONO_VERBOSE" != "1" ]; then
            warn "Re-run with OPENMONO_VERBOSE=1 for detailed output."
        fi
    fi
fi  # End of Step 8 (skipped on agent role)

# ── Shell integration ─────────────────────────────────────────────────────────
# Put the openmono CLI on PATH so `openmono <cmd>` resolves to the repo script.
# IMPORTANT: we use a PATH entry (not an alias) because an alias would shadow
# all subcommands with a single fixed invocation.

# Install a symlink to /usr/local/bin when possible so the CLI is
# immediately available (no shell reload needed).
if [ -w /usr/local/bin ]; then
    if ln -sf "$INSTALL_DIR/openmono" /usr/local/bin/openmono 2>/dev/null; then
        detail "Symlinked /usr/local/bin/openmono -> $INSTALL_DIR/openmono"
    else
        warn "Could not create /usr/local/bin/openmono symlink (permission issue)"
    fi
else
    detail "/usr/local/bin is not writable — shell integration will be handled by openmono cmd_setup"
fi

# Installation completed successfully — clear persisted setup choices so a
# future `openmono setup` run starts fresh rather than restoring stale prefs.
clear_setup_prefs

# Write environment to the file passed by openmono cmd_setup
if [[ -n "${OPENMONO_ENV_FILE:-}" ]]; then
    cat > "$OPENMONO_ENV_FILE" <<ENVEOF
export INSTALL_DIR="$INSTALL_DIR"
export LLAMA_PORT="${LLAMA_PORT:-7474}"
export GPU_MODE="${GPU_MODE:-0}"
export OPENMONO_ROLE="$OPENMONO_ROLE"
export MODEL_NAME="${MODEL_NAME:-}"
export MODEL_ACCURACY="${MODEL_ACCURACY:-standard}"
ENVEOF
    _log "Wrote install environment to: $OPENMONO_ENV_FILE"
else
    warn "OPENMONO_ENV_FILE not set (openmono cmd_setup should have set this)"
fi