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
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
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
if [[ -z "${OPENMONO_DOCKER_SG_REEXEC:-}" ]] \
   && command -v docker &>/dev/null \
   && ! docker info &>/dev/null 2>&1 \
   && command -v getent &>/dev/null \
   && getent group docker 2>/dev/null | grep -qw "$(id -un)"; then
    echo "[INFO] Activating docker group for this session via 'sg docker'..."
    export OPENMONO_DOCKER_SG_REEXEC=1
    exec sg docker -c "bash \"$0\" $*"
fi

# Role selector — drives which of the 8 install steps actually run.
# If the caller (openmono setup) already exported OPENMONO_ROLE, use it.
# Otherwise prompt — this handles the ./scripts/install.sh direct-run path
# where the openmono CLI doesn't exist yet.
if [[ -z "${OPENMONO_ROLE:-}" ]]; then
    echo ""
    echo "  What do you want to install on this machine?"
    echo ""
    echo "  1) Both — agent + inference server on one box (single-box mode)"
    echo "  2) Inference server only — GPU box that runs the model"
    echo "             (pair with a separate agent box via openmono tunnel)"
    echo "  3) Agent only — laptop/workstation that talks to a remote inference server"
    echo "             (dual-box mode; point at inference box with openmono config)"
    echo ""
    while true; do
        printf "  Enter 1, 2 or 3 [default: 1]: "
        read -r _role_choice
        _role_choice="${_role_choice:-1}"
        case "$_role_choice" in
            1) OPENMONO_ROLE=full      ; break ;;
            2) OPENMONO_ROLE=inference ; break ;;
            3) OPENMONO_ROLE=agent     ; break ;;
            *) echo "  Please enter 1, 2, or 3." ;;
        esac
    done
    echo ""
fi

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
        # Docker permission issue is critical - can't continue
        if ! docker info &>/dev/null 2>&1; then
            err "Docker is installed but not accessible without sudo."
            err "This will cause the installation to fail."
            echo ""
            err "To fix this, run one of the following:"
            err "  1. newgrp docker              (if already in docker group)"
            err "  2. sudo usermod -aG docker \$USER && newgrp docker"
            err "  3. Log out and back in"
            echo ""
            die "Cannot continue without docker access."
        fi
    fi

    ok "All prerequisites satisfied"
}

# ── Docker group auto-activation ─────────────────────────────────────────────
# If docker is installed but not accessible (user was just added to the group by
# install_prereqs.sh), re-exec this script under 'sg docker' so all docker
# commands work without sudo — no manual 'newgrp docker' step required.
if command -v docker &>/dev/null && ! docker info &>/dev/null 2>&1; then
    if id -nG 2>/dev/null | grep -qw docker && command -v sg &>/dev/null; then
        if sg docker -c "docker info" &>/dev/null 2>&1; then
            info "Docker group not yet active — re-launching installer with it active..."
            exec sg docker -- bash "$0" "$@"
        fi
    fi
fi

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

# RAM — only matters on machines that will load the 18.5 GB model
if command -v free &>/dev/null; then
    TOTAL_MEM=$(free -g | awk '/^Mem:/{print $2}')
    if [ "$OPENMONO_ROLE" = "agent" ]; then
        ok "RAM: ${TOTAL_MEM}GB (no model loaded locally on agent box)"
    elif [ "$TOTAL_MEM" -lt 20 ]; then
        warn "Only ${TOTAL_MEM}GB RAM detected (model needs ~20GB). It may be slow or fail to load."
    else
        ok "RAM: ${TOTAL_MEM}GB"
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

# Early GPU sniff — used to select the right model before Step 6 does full validation.
# GPU:  Qwen3.6-27B-Q4_K_M  (dense 27B, needs VRAM for speed)
# CPU:  Qwen3.6-35B-A3B-UD-Q4_K_XL (MoE, only 3.5B active params — fast on CPU)
_EARLY_GPU=false
if [ "${OPENMONO_GPU:-}" = "1" ]; then
    _EARLY_GPU=true
elif [ "${OPENMONO_CPU:-}" != "1" ] && command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null 2>&1; then
    docker info 2>/dev/null | grep -q nvidia && _EARLY_GPU=true
fi

# ── Step 4: Download model (inference + full only) ───────────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    MODEL_DIR="$INSTALL_DIR/models"

    if [ "$_EARLY_GPU" = true ]; then
        MODEL_NAME="Qwen3.6-27B-Q4_K_M.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-27B-GGUF/resolve/main/Qwen3.6-27B-Q4_K_M.gguf"
        next_step "Downloading Qwen3.6-27B-Q4_K_M model (~15 GB) [GPU]"
    else
        MODEL_NAME="qwen3.6-35b-a3b-ud-q4_k_xl.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
        next_step "Downloading Qwen3.6-35B-A3B model (~17.6 GB) [CPU]"
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

# ── Step 6: Detect/configure GPU mode (inference + full only) ────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Detecting GPU / CPU mode"

HAS_GPU=false
if [ "${OPENMONO_GPU:-}" = "1" ]; then
    info "GPU mode forced via --gpu"
    HAS_GPU=true
elif [ "${OPENMONO_CPU:-}" = "1" ]; then
    info "CPU mode forced via --cpu"
    HAS_GPU=false
elif command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null 2>&1; then
    if docker info 2>/dev/null | grep -q nvidia; then
        info "NVIDIA GPU + nvidia runtime detected"
        HAS_GPU=true
    else
        warn "NVIDIA GPU detected, but Docker is not configured with the nvidia runtime."
        warn "Run: openmono setup  (installs nvidia-container-toolkit)"
        warn "Falling back to CPU mode."
    fi
else
    if [ "${HAS_NVIDIA_HW:-false}" = true ]; then
        warn "NVIDIA GPU hardware detected, but drivers are not working or not installed."
        warn "Falling back to CPU mode."
    else
        info "No NVIDIA GPU detected — using CPU mode"
    fi
fi

OVERRIDE_FILE="$INSTALL_DIR/docker/docker-compose.override.yml"
# Derive a clean alias from the filename (strip .gguf) so /props returns the right name
MODEL_ALIAS="${MODEL_NAME%.gguf}"

if [ "$HAS_GPU" = true ]; then
    info "Writing GPU override: $OVERRIDE_FILE"
    cat > "$OVERRIDE_FILE" <<EOF
# GPU configuration (auto-generated by install.sh)
services:
  llama-server:
    image: ghcr.io/ggml-org/llama.cpp:server-cuda
    command: >
      --model /models/$MODEL_NAME
      --alias $MODEL_ALIAS
      --host 0.0.0.0
      --port 7474
      --ctx-size 196608
      --threads 14
      --n-gpu-layers 99
      --flash-attn on
      --cache-type-k q8_0
      --cache-type-v q8_0
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
    if [ "${HAS_GPU:-false}" = true ]; then
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

for rc in "$HOME/.bashrc" "$HOME/.zshrc"; do
    if [ -f "$rc" ]; then
        # Clean up any prior OpenMono block, including legacy alias forms.
        if grep -q "# OpenMono.ai" "$rc"; then
            # Remove from "# OpenMono.ai" up to the next blank line
            sed -i '/# OpenMono.ai/,/^$/d' "$rc"
        fi
        # Also strip any stale top-level `alias openmono=` line from older installs
        sed -i '/^alias openmono=/d' "$rc"

        {
            echo ""
            echo "# OpenMono.ai"
            echo "export LLAMA_PORT=${LLAMA_PORT:-7474}"
            echo "export PATH=\"$INSTALL_DIR:\$PATH\""
        } >> "$rc"
        detail "PATH updated in $(basename "$rc")"
    fi
done

# Also install a symlink to /usr/local/bin when possible so the CLI is
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

# ── Done ──────────────────────────────────────────────────────────────────────

echo ""
printf "${GREEN}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
printf "${GREEN}${BOLD}  Installation Complete${NC} (role: %s)\n" "$OPENMONO_ROLE"
printf "${GREEN}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
echo ""

case "$OPENMONO_ROLE" in
    full)
        echo "  llama-server port : ${LLAMA_PORT:-7474}"
        echo "  model             : ${MODEL_FILE:-n/a}"
        echo "  mode              : $([ "${HAS_GPU:-false}" = true ] && echo GPU || echo CPU)"
        echo ""
        echo "Next steps:"
        echo "  1. source ~/.bashrc        # Reload shell"
        echo "  2. cd your-project/"
        echo "  3. openmono agent          # Start the coding agent"
        ;;
    inference)
        echo "  llama-server port : ${LLAMA_PORT:-7474}"
        echo "  model             : ${MODEL_FILE:-n/a}"
        echo "  mode              : $([ "${HAS_GPU:-false}" = true ] && echo GPU || echo CPU)"
        echo ""
        echo "This machine is now the inference server. To make it reachable"
        echo "from an agent box over the internet, connect it to a relay server."
        echo "Need a relay? Sign up for FREE at https://app.openmonoagent.ai to get started!"
        echo ""
        echo "Already have your token? Run the following to set up the tunnel:"
        echo "  openmono tunnel setup"
        echo ""
        ;;
    agent)
        echo "  agent binary : built in Docker (openmono-agent image)"
        echo ""
        echo "Next steps:"
        echo "  1. source ~/.bashrc        # Reload shell"
        echo "  2. Point this agent at your inference server:"
        echo "     Get the commands from the inference server setup instructions."
        echo "     Example:"
        echo "        openmono config set llm.endpoint http://<server>:<port>"
        echo "        openmono config set llm.api_key  <token>"
        echo "  3. cd your-project/ && openmono agent"
        echo ""        
        ;;
esac
echo ""
show_log_location
