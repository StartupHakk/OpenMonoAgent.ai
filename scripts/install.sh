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

# Source the shared env file written by install_prereqs.sh (GPU_MODE, etc.)
# shellcheck source=/dev/null
if [[ -n "${OPENMONO_ENV_FILE:-}" ]] && [[ -f "$OPENMONO_ENV_FILE" ]]; then
    source "$OPENMONO_ENV_FILE"
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
# Query VRAM to determine the right model tier. Only for roles that load a model.
# Tiers:  24GB+ → Qwen3.6-27B Q4  (full accuracy)
#         16GB  → Qwen3.6-35B-A3B Q3 + q4 kv cache  (lower accuracy)
#         12GB  → Qwen3.5-9B Q4 + q4 kv cache        (lower accuracy)
#         CPU   → Qwen3.6-35B-A3B Q4_K_XL (MoE, 3.5B active params)
_VRAM_MB=0
_GPU_TIER=0
MODEL_NAME=""
MODEL_ACCURACY=""
if [ "$OPENMONO_ROLE" != "agent" ]; then
    if [ "${GPU_MODE:-0}" = "1" ]; then
        if command -v nvidia-smi &>/dev/null; then
            _VRAM_MB=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | awk 'NR==1{print $1}')
            _VRAM_MB=${_VRAM_MB:-0}
            if   [ "$_VRAM_MB" -ge 24000 ]; then _GPU_TIER=24; ok "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — full accuracy model (Qwen3.6-27B)"
            elif [ "$_VRAM_MB" -ge 16000 ]; then _GPU_TIER=16; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — lower accuracy model (Qwen3.6-35B-A3B Q3 + q4 kv cache). For best results use 24GB+ VRAM."
            elif [ "$_VRAM_MB" -ge 12000 ]; then _GPU_TIER=12; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — lower accuracy model (Qwen3.5-9B Q4 + q4 kv cache). For best results use 24GB+ VRAM."
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

    if [ "$_GPU_TIER" -eq 24 ]; then
        MODEL_NAME="Qwen3.6-27B-Q4_K_M.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/Qwen3.6-27B-Q4_K_M.gguf"
        MODEL_ACCURACY="full"
        next_step "Downloading Qwen3.6-27B-Q4_K_M (~15GB) [GPU 24GB+ — full accuracy]"
    elif [ "$_GPU_TIER" -eq 16 ]; then
        MODEL_NAME="Qwen3.6-27B-UD-IQ3_XXS.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/Qwen3.6-27B-UD-IQ3_XXS.gguf"
        MODEL_ACCURACY="lower"
        next_step "Downloading Qwen3.6-27B-UD-IQ3_XXS (~12GB) [GPU 16GB — lower accuracy]"
    elif [ "$_GPU_TIER" -eq 12 ]; then
        MODEL_NAME="Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_ACCURACY="lower"
        next_step "Downloading Qwen3.5-9B-Q4_K_M (~5GB) [GPU 12GB — lower accuracy]"
    else
        MODEL_NAME="qwen3.6-35b-a3b-ud-q4_k_xl.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
        MODEL_ACCURACY="standard"
        next_step "Downloading Qwen3.6-35B-A3B (~17.6GB) [CPU]"
    fi

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
    else
        info "CPU mode"
    fi

OVERRIDE_FILE="$INSTALL_DIR/docker/docker-compose.override.yml"
# Derive a clean alias from the filename (strip .gguf) so /props returns the right name
MODEL_ALIAS="${MODEL_NAME%.gguf}"

if [ "${GPU_MODE:-0}" = "1" ]; then
    # 24GB tier: q8 kv cache (high quality); 16GB/12GB tiers: q4 kv cache (saves VRAM)
    if [ "$_GPU_TIER" -ge 24 ]; then
        _KV_K="q8_0"; _KV_V="q8_0"
        _CTX=196608
    elif [ "$_GPU_TIER" -ge 16 ]; then
        _KV_K="q4_0"; _KV_V="q4_0"
        _CTX=180224
    else
        _KV_K="q4_0"; _KV_V="q4_0"
        _CTX=196608
    fi
    [ "$MODEL_ACCURACY" = "lower" ] && info "Lower accuracy model selected — q4 kv cache enabled to fit $(( (_VRAM_MB + 512) / 1024 ))GB VRAM"
    info "Writing GPU override: $OVERRIDE_FILE"
    cat > "$OVERRIDE_FILE" <<EOF
# GPU configuration (auto-generated by install.sh — ${MODEL_ACCURACY:-full} accuracy)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-cuda
    command: >
      --model /models/$MODEL_NAME
      --alias $MODEL_ALIAS
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
      --metrics
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
else
    info "Writing CPU override: $OVERRIDE_FILE"
    # Thread count tuned to physical cores (SMT hurts llama.cpp throughput).
    CPU_THREADS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 8)"
    PHYS_CORES="$(lscpu -b -p=Core,Socket 2>/dev/null | grep -v '^#' | sort -u | wc -l)"
    [ "${PHYS_CORES:-0}" -gt 0 ] && CPU_THREADS="$PHYS_CORES"
    cat > "$OVERRIDE_FILE" <<EOF
# CPU/Vulkan configuration (auto-generated by install.sh)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-vulkan
    command: >
      --model /models/$MODEL_NAME
      --alias $MODEL_ALIAS
      --host 0.0.0.0
      --port 7474
      --ctx-size 196608
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
      --metrics
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
EOF
    ok "CPU override written"

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

# Shell rc file updates are handled by openmono cmd_setup after installation completes
# This ensures we only update the appropriate files for the user's actual shell

# Install a symlink to /usr/local/bin when possible so the CLI is
# immediately available (no shell reload needed). Soft-fail if not writable.
if [ -w /usr/local/bin ] || [ -n "${SUDO:-}" ]; then
    if [ -w /usr/local/bin ]; then
        ln -sf "$INSTALL_DIR/openmono" /usr/local/bin/openmono 2>/dev/null && \
            detail "Symlinked /usr/local/bin/openmono -> $INSTALL_DIR/openmono"
    else
        sudo ln -sf "$INSTALL_DIR/openmono" /usr/local/bin/openmono 2>/dev/null && \
            detail "Symlinked /usr/local/bin/openmono -> $INSTALL_DIR/openmono"
    fi
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