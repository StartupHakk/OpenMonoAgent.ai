#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai Installer (macOS)
#
# Builds Docker images, downloads the model, and starts the llama-server daemon.
# Assumes prerequisites (Docker, curl, .NET, etc.) are already installed by
# scripts/install_prereqs_macos.sh.
#
# Options (via env or openmono CLI flags):
#   OPENMONO_ROLE         Install role: full (default), inference, or agent
#   OPENMONO_VERBOSE=1    Show detailed command output
#   LLAMA_PORT=7474       llama-server host port (default 7474)
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

# Role selector — drives which of the 8 install steps actually run.
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

# Step counts vary by role.
case "$OPENMONO_ROLE" in
    full)      TOTAL_STEPS=8 ;;
    inference) TOTAL_STEPS=7 ;;
    agent)     TOTAL_STEPS=5 ;;
esac

banner "OpenMono.ai Installer (role: $OPENMONO_ROLE) — macOS"

CURRENT_STEP=0
next_step() {
    CURRENT_STEP=$((CURRENT_STEP + 1))
    step $CURRENT_STEP $TOTAL_STEPS "$1"
}

# ── Prerequisite Check ────────────────────────────────────────────────────────

check_prerequisites() {
    local missing=()
    local warnings=()

    # Required commands
    command -v docker &>/dev/null || missing+=("docker")
    command -v git &>/dev/null || missing+=("git")
    command -v curl &>/dev/null || missing+=("curl")
    command -v cmake &>/dev/null || missing+=("cmake")

    # Docker Compose (check hyphenated version on macOS, space-separated on Linux)
    if ! docker-compose --version &>/dev/null 2>&1 && ! docker compose --version &>/dev/null 2>&1; then
        missing+=("docker-compose")
    fi

    # Check if user can run docker
    if command -v docker &>/dev/null; then
        if ! docker info &>/dev/null 2>&1; then
            warnings+=("Docker is installed but not accessible. Try: sudo systemctl restart docker (or restart Docker Desktop)")
        fi
    fi

    # .NET SDK (optional but recommended)
    if ! command -v dotnet &>/dev/null; then
        warnings+=(".NET SDK not installed (optional, but recommended)")
    fi

    # Report results
    if [ ${#missing[@]} -gt 0 ]; then
        err "Missing required prerequisites:"
        for pkg in "${missing[@]}"; do
            printf "  ${RED}✗${NC}  %s\n" "$pkg"
        done
        echo ""
        err "Please run the prerequisites installer first:"
        err "  ./scripts/install_prereqs_macos.sh"
        echo ""
        die "Cannot continue without required prerequisites."
    fi

    if [ ${#warnings[@]} -gt 0 ]; then
        warn "Prerequisite warnings:"
        for w in "${warnings[@]}"; do
            printf "  ${YELLOW}⚠${NC}  %s\n" "$w"
        done
        echo ""
        if ! docker info &>/dev/null 2>&1; then
            err "Docker is installed but not accessible."
            die "Please restart Docker Desktop and try again."
        fi
    fi

    ok "All prerequisites satisfied"
}

info "Checking prerequisites..."
check_prerequisites

# ── Step 1: Resolve install directory ──────────────────────────────────────────

next_step "Resolving install directory"

if [ -f "$SCRIPT_DIR/../OpenMono.sln" ]; then
    INSTALL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
elif [ -n "${OPENMONO_HOME:-}" ]; then
    INSTALL_DIR="$OPENMONO_HOME"
else
    INSTALL_DIR="$HOME/openmono.ai"
fi

ok "Install directory: $INSTALL_DIR"

# ── Step 2: Check system requirements ──────────────────────────────────────────

next_step "Checking system requirements"

# RAM — only matters on machines that will load the 18.5 GB model
if command -v sysctl &>/dev/null; then
    TOTAL_MEM=$(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1024 / 1024 / 1024 ))
    if [ "$OPENMONO_ROLE" = "agent" ]; then
        ok "RAM: ${TOTAL_MEM}GB (no model loaded locally on agent box)"
    elif [ "$TOTAL_MEM" -lt 20 ]; then
        warn "Only ${TOTAL_MEM}GB RAM detected (model needs ~20GB). It may be slow or fail to load."
    else
        ok "RAM: ${TOTAL_MEM}GB"
    fi
fi

# Display tool versions
detail "docker: $(docker --version 2>/dev/null | head -1)"
detail "git: $(git --version 2>/dev/null)"
detail "curl: $(curl --version 2>/dev/null | head -1)"
if command -v docker-compose &>/dev/null; then
    detail "docker-compose: $(docker-compose --version 2>/dev/null)"
elif command -v docker &>/dev/null && docker compose --version &>/dev/null 2>&1; then
    detail "docker compose: $(docker compose --version 2>/dev/null)"
fi

ok "System requirements verified"

# ── Step 3: Fetch repo if missing ──────────────────────────────────────────────

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

# ── Step 4: Download model (inference + full only) ────────────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Downloading Qwen3.6-35B-A3B model (~18.5 GB)"

    MODEL_DIR="$INSTALL_DIR/models"
    MODEL_NAME="qwen3.6-35b-a3b-ud-q4_k_xl.gguf"
    MODEL_FILE="$MODEL_DIR/$MODEL_NAME"
    MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
    MODEL_MIN_BYTES=$((1024 * 1024 * 1024))  # 1 GB sanity check (real file ~18.5 GB)

    mkdir -p "$MODEL_DIR"

    model_size() { stat -f%z "$1" 2>/dev/null || echo 0; }

    if [ -f "$MODEL_FILE" ] && [ "$(model_size "$MODEL_FILE")" -gt "$MODEL_MIN_BYTES" ]; then
        ok "Model already present ($(du -h "$MODEL_FILE" | awk '{print $1}'))"
    else
        if [ -f "$MODEL_FILE" ]; then
            warn "Existing model file looks incomplete ($(du -h "$MODEL_FILE" | awk '{print $1}')) — removing"
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

        ok "Model downloaded ($(du -h "$MODEL_FILE" | awk '{print $1}'))"
    fi
fi

# ── Step 5: code-review-graph (agent + full only) ────────────────────────────

if [ "$OPENMONO_ROLE" != "inference" ]; then
    next_step "Setting up code-review-graph"

    if command -v code-review-graph &>/dev/null; then
        ok "code-review-graph already installed"
    elif command -v pip3 &>/dev/null; then
        info "Installing code-review-graph via pip3..."
        if run pip3 install --user code-review-graph; then
            ok "code-review-graph installed"
        elif run pip3 install --user --break-system-packages code-review-graph; then
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
    elif command -v pip3 &>/dev/null; then
        info "Installing graphify via pip3..."
        if run pip3 install --user graphifyy; then
            ok "graphify installed"
        elif run pip3 install --user --break-system-packages graphifyy; then
            ok "graphify installed (--break-system-packages)"
        else
            warn "Could not install graphify via pip — install manually: pip install graphifyy && graphify install"
        fi
    else
        warn "Skipping graphify install (no pip3). Install manually: pip install graphifyy && graphify install"
    fi
fi

# ── Step 6: Docker configuration (inference + full only) ──────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Configuring Docker for inference"

    ARCH=$(uname -m)
    if [ "$ARCH" = "arm64" ]; then
        ok "Apple Silicon (M-series) detected — using CPU mode in Docker"
    else
        ok "Intel Mac detected — using CPU mode"
    fi

    # On macOS, Docker runs in a Linux VM — no direct Metal GPU passthrough.
    # We always use CPU mode, tuned to physical core count.
    OVERRIDE_FILE="$INSTALL_DIR/docker/docker-compose.override.yml"
    info "Writing CPU override: $OVERRIDE_FILE"

    # Detect physical core count (macOS specific)
    CPU_THREADS="$(sysctl -n hw.physicalcpu 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || echo 8)"

    # Derive model alias (strip .gguf extension)
    MODEL_ALIAS="${MODEL_NAME%.gguf}"

    cat > "$OVERRIDE_FILE" <<EOF
# CPU configuration — macOS (Docker runs in Linux VM, no Metal GPU passthrough)
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
    ok "CPU override written (threads: $CPU_THREADS, context: 196608)"
fi

# ── Step 7: Build Docker images ────────────────────────────────────────────────

next_step "Building Docker images"

cd "$INSTALL_DIR/docker"

# Determine which docker compose command to use (prefer v2 plugin over v1 standalone)
if docker compose version &>/dev/null 2>&1; then
    DOCKER_COMPOSE_CMD="docker compose"
elif command -v docker-compose &>/dev/null; then
    DOCKER_COMPOSE_CMD="docker-compose"
else
    die "No Docker Compose found. Run: openmono setup to install prerequisites."
fi

info "Stopping any running containers..."
run $DOCKER_COMPOSE_CMD down || true

# Only build the images this role actually needs.
if [ "$OPENMONO_ROLE" != "agent" ]; then
    info "Building llama-server image..."
    if ! run $DOCKER_COMPOSE_CMD build llama-server; then
        die "llama-server build failed"
    fi
fi

if [ "$OPENMONO_ROLE" != "inference" ]; then
    info "Building agent image..."
    if ! run $DOCKER_COMPOSE_CMD build agent; then
        die "agent build failed"
    fi
fi

ok "Docker images built"

# ── Step 8: Start llama-server (inference + full only) ────────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Starting llama-server"

    LLAMA_PORT="${LLAMA_PORT:-7474}"

    port_in_use() {
        lsof -i ":${1}" &>/dev/null 2>&1
    }

    if port_in_use "$LLAMA_PORT"; then
        warn "Port ${LLAMA_PORT} is in use"
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
    if ! run $DOCKER_COMPOSE_CMD up -d llama-server; then
        die "Failed to start llama-server (check: $DOCKER_COMPOSE_CMD logs llama-server)"
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
        warn "This can be normal on systems with limited RAM (model is ~20 GB)."
        warn "Check: openmono logs"
        if [ "$OPENMONO_VERBOSE" != "1" ]; then
            warn "Re-run with OPENMONO_VERBOSE=1 for detailed output."
        fi
    fi
fi

# ── Shell integration ─────────────────────────────────────────────────────────
# Put the openmono CLI on PATH so `openmono <cmd>` resolves to the repo script.
# On macOS, prioritize ~/.zshrc (default shell), also update ~/.bash_profile.

for rc in "$HOME/.zshrc" "$HOME/.bash_profile" "$HOME/.bashrc"; do
    if [ -f "$rc" ]; then
        # Clean up any prior OpenMono block
        if grep -q "# OpenMono.ai" "$rc"; then
            sed -i '' '/# OpenMono.ai/,/^$/d' "$rc"
        fi
        # Strip any stale top-level `alias openmono=` line
        sed -i '' '/^alias openmono=/d' "$rc"

        {
            echo ""
            echo "# OpenMono.ai"
            echo "export LLAMA_PORT=${LLAMA_PORT:-7474}"
            echo "export PATH=\"$INSTALL_DIR:\$PATH\""
        } >> "$rc"
        detail "PATH updated in $(basename "$rc")"
    fi
done

# Symlink to /usr/local/bin if writable (standard on both Intel and Apple Silicon Macs)
if [ -w /usr/local/bin ]; then
    ln -sf "$INSTALL_DIR/openmono" /usr/local/bin/openmono 2>/dev/null && \
        detail "Symlinked /usr/local/bin/openmono -> $INSTALL_DIR/openmono"
elif [ -n "${SUDO:-}" ] && [ -x "$(command -v sudo)" ]; then
    sudo ln -sf "$INSTALL_DIR/openmono" /usr/local/bin/openmono 2>/dev/null && \
        detail "Symlinked /usr/local/bin/openmono -> $INSTALL_DIR/openmono"
fi

# ── Done ───────────────────────────────────────────────────────────────────────

echo ""
printf "${GREEN}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
printf "${GREEN}${BOLD}  Installation Complete${NC} (role: %s, platform: macOS)\n" "$OPENMONO_ROLE"
printf "${GREEN}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
echo ""

case "$OPENMONO_ROLE" in
    full)
        echo "  llama-server port : ${LLAMA_PORT:-7474}"
        echo "  model             : ${MODEL_FILE:-n/a}"
        echo "  mode              : CPU (Docker Linux VM)"
        echo ""
        echo "Next steps:"
        echo "  1. source ~/.zshrc         # Reload shell"
        echo "  2. cd your-project/"
        echo "  3. openmono agent          # Start the coding agent"
        ;;
    inference)
        echo "  llama-server port : ${LLAMA_PORT:-7474}"
        echo "  model             : ${MODEL_FILE:-n/a}"
        echo "  mode              : CPU (Docker Linux VM)"
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
        echo "  1. source ~/.zshrc         # Reload shell"
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
