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
INSTALL_DIR="$(dirname "$SCRIPT_DIR")"  # repo root (parent of scripts/)
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

# ── Context size presets (tokens) — edit here to change globally ──────────────
CTX_48G=196608       # >=48 GB Metal  q8_0 KV
CTX_32G=65536        # 32 GB Metal
CTX_16G=16384        # 16 GB Metal
CTX_8G=8192          # <16 GB Metal
CTX_VISION=172032    # >=48 GB + mmproj  (encoder needs ~474 MiB burst)
CTX_VISION_32G=49152 # 32 GB + mmproj   (~25% reduction)
CTX_VISION_16G=12288 # 16 GB + mmproj   (~25% reduction)

# Selects the right model for the host's Apple Silicon unified memory tier.
# Arg $1: host RAM in GB (integer). Sets MODEL_NAME, MODEL_URL, MODEL_ACCURACY,
# MODEL_ALIAS, _MODEL_LABEL, _CTX_SIZE.
#
# Architecture refs (Qwen3 technical report):
#   35B A3B MoE  — 94 layers, 4 KV heads, head_dim 128 → KV ~18.5 GB @ q8_0/196608
#   27B dense    — 52 layers, 8 KV heads, head_dim 128 → KV  ~6.5 GB @ q8_0/65536
#   9B dense     — 36 layers, 8 KV heads, head_dim 128 → KV  ~1.1 GB @ q8_0/16384
select_model() {
    local ram_gb="${1:-0}"
    if [ "$ram_gb" -ge 48 ]; then
        # ~17.6 GB weights + ~18.5 GB KV + ~6 GB OS ≈ 42 GB on 48 GB Mac
        MODEL_NAME="Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf"
        MODEL_ACCURACY="standard"
        MODEL_MMPROJ="mmproj-qwen3.6-35B-A3B-F16.gguf"
        MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF/resolve/main/mmproj-F16.gguf"
        _MODEL_LABEL="Qwen3.6-35B-A3B (~17.6GB) [Apple Silicon >=48GB — standard]"
        _CTX_SIZE=$CTX_48G
    elif [ "$ram_gb" -ge 32 ]; then
        # ~5 GB weights + ~1.1 GB KV + ~6 GB OS ≈ 12 GB on 32 GB Mac
        MODEL_NAME="Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_ACCURACY="lower"
        MODEL_MMPROJ="mmproj-qwen3.5-9B-F16.gguf"
        MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/mmproj-F16.gguf"
        _MODEL_LABEL="Qwen3.5-9B-Q4_K_M (~5GB) [Apple Silicon 32GB]"
        _CTX_SIZE=$CTX_32G
    elif [ "$ram_gb" -ge 16 ]; then
        # ~5 GB weights + ~1.1 GB KV + ~6 GB OS ≈ 12 GB on 16 GB Mac
        MODEL_NAME="Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_ACCURACY="lower"
        MODEL_MMPROJ="mmproj-qwen3.5-9B-F16.gguf"
        MODEL_MMPROJ_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/mmproj-F16.gguf"
        _MODEL_LABEL="Qwen3.5-9B-Q4_K_M (~5GB) [Apple Silicon 16GB — lower accuracy]"
        _CTX_SIZE=$CTX_16G
    else
        MODEL_NAME="Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_URL="https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf"
        MODEL_ACCURACY="lower"
        MODEL_MMPROJ=""
        MODEL_MMPROJ_URL=""
        _MODEL_LABEL="Qwen3.5-9B-Q4_K_M (~5GB) [Apple Silicon <16GB]"
        _CTX_SIZE=$CTX_8G
    fi
    MODEL_ALIAS="${MODEL_NAME%.gguf}"
}

# Initialize Homebrew in PATH (installed by install_prereqs_macos.sh)
# Detect architecture to set correct Homebrew prefix
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    BREW_PREFIX="/opt/homebrew"
else
    BREW_PREFIX="/usr/local"
fi

if [ -d "$BREW_PREFIX" ]; then
    eval "$("$BREW_PREFIX"/bin/brew shellenv)"
    export PATH="$BREW_PREFIX/bin:$PATH"
fi

# Intel Macs have no unified memory and no Metal GPU — inference is unsupported.
if [ "$ARCH" != "arm64" ]; then
    if [ "${OPENMONO_ROLE:-}" = "full" ] || [ "${OPENMONO_ROLE:-}" = "inference" ]; then
        die "Full/inference roles require Apple Silicon (arm64). Intel Mac detected.
Use the 'agent' role on this machine and point it at a separate inference server."
    fi
fi

# Role selector — drives which of the install steps actually run.
# If the caller (openmono setup) already exported OPENMONO_ROLE, use it.
# Otherwise prompt — this handles the direct-run path where openmono CLI isn't available yet.
role_prompt

case "$OPENMONO_ROLE" in
    full|inference|agent) ;;
    *) echo "ERROR: Invalid OPENMONO_ROLE='$OPENMONO_ROLE' (expected: full, inference, agent)" >&2; exit 1 ;;
esac

# Step counts vary by role.
case "$OPENMONO_ROLE" in
    full)      TOTAL_STEPS=8 ;;
    inference) TOTAL_STEPS=6 ;;
    agent)     TOTAL_STEPS=5 ;;
esac

banner "OpenMono.ai Installer (role: $OPENMONO_ROLE) — macOS"

CURRENT_STEP=0
next_step() {
    CURRENT_STEP=$((CURRENT_STEP + 1))
    step "$CURRENT_STEP" "$TOTAL_STEPS" "$1"
}

# ── Prerequisite Check ────────────────────────────────────────────────────────

check_prerequisites() {
    local missing=()
    local warnings=()

    # Required commands (unconditional)
    command -v git &>/dev/null || missing+=("git")
    command -v curl &>/dev/null || missing+=("curl")
    command -v jq &>/dev/null || missing+=("jq")

    # Docker/Docker Compose only needed for full and agent roles (not inference)
    if [ "$OPENMONO_ROLE" != "inference" ]; then
        command -v docker &>/dev/null || missing+=("docker")
        if ! docker-compose --version &>/dev/null 2>&1 && ! docker compose --version &>/dev/null 2>&1; then
            missing+=("docker-compose")
        fi
        if command -v docker &>/dev/null && ! docker info &>/dev/null 2>&1; then
            warnings+=("Docker is installed but not accessible. Try: sudo systemctl restart docker (or restart Docker Desktop)")
        fi
    fi

    # cmake only needed for inference role
    if [ "$OPENMONO_ROLE" != "agent" ]; then
        command -v cmake &>/dev/null || missing+=("cmake")
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
        if [ "$OPENMONO_ROLE" != "inference" ] && ! docker info &>/dev/null 2>&1; then
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

# Detect unified memory and select model for this machine's tier.
TOTAL_MEM=0
if command -v sysctl &>/dev/null; then
    TOTAL_MEM=$(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1024 / 1024 / 1024 ))
fi

if [ "$OPENMONO_ROLE" != "agent" ]; then
    if [ "$TOTAL_MEM" -lt 16 ]; then
        die "Only ${TOTAL_MEM}GB RAM detected. 16GB unified memory is the minimum for inference.
Upgrade to a Mac with at least 16GB, or use the 'agent' role and connect to a separate inference server."
    fi
    select_model "$TOTAL_MEM"

    # Reduce context when mmproj is active (encoder needs ~1-2 GB burst)
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        _CTX_ORIG=$_CTX_SIZE
        if   [ "$TOTAL_MEM" -ge 48 ]; then _CTX_SIZE=$CTX_VISION
        elif [ "$TOTAL_MEM" -ge 32 ]; then _CTX_SIZE=$CTX_VISION_32G
        else                                _CTX_SIZE=$CTX_VISION_16G; fi
        detail "Vision enabled: context reduced from $((_CTX_ORIG/1024))k → $((_CTX_SIZE/1024))k to fit mmproj"
    fi
    ok "Apple Silicon ${TOTAL_MEM}GB → $_MODEL_LABEL (ctx: $_CTX_SIZE)"
fi

# Detect pip/pip3 for optional python deps (should be available from install_prereqs)
PIP_CMD=""
if command -v pip3 &>/dev/null; then
    PIP_CMD="pip3"
elif command -v pip &>/dev/null; then
    PIP_CMD="pip"
fi
[ -z "$PIP_CMD" ] && warn "pip/pip3 not found — optional python deps will be skipped on host"

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
    next_step "Downloading $_MODEL_LABEL"

    MODEL_DIR="$INSTALL_DIR/models"
    MODEL_FILE="$MODEL_DIR/$MODEL_NAME"
    # MODEL_NAME and MODEL_URL set by select_model() in Step 2
    MODEL_MIN_BYTES=$((1024 * 1024 * 1024))  # 1 GB sanity check

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

    # ── mmproj (vision projector) ─────────────────────────────────────────────
    if [ -z "${MODEL_MMPROJ:-}" ]; then
        info "No mmproj configured for this model — vision disabled"
    else
        MMPROJ_FILE="$MODEL_DIR/$MODEL_MMPROJ"
        MMPROJ_MIN_BYTES=$((100 * 1024 * 1024))
        if [ -f "$MMPROJ_FILE" ] && [ "$(stat -f%z "$MMPROJ_FILE" 2>/dev/null || echo 0)" -gt "$MMPROJ_MIN_BYTES" ]; then
            ok "mmproj already present ($(du -h "$MMPROJ_FILE" | awk '{print $1}'))"
        else
            [ -f "$MMPROJ_FILE" ] && { warn "Existing mmproj incomplete — removing"; rm -f "$MMPROJ_FILE"; }
            info "Downloading mmproj: $MODEL_MMPROJ (~900 MB)"
            if ! run_live curl -L --fail --progress-bar -o "$MMPROJ_FILE" "$MODEL_MMPROJ_URL"; then
                rm -f "$MMPROJ_FILE"
                warn "mmproj download failed — vision will be unavailable"
                MODEL_MMPROJ=""
                MMPROJ_FILE=""
            else
                ok "mmproj downloaded ($(du -h "$MMPROJ_FILE" | awk '{print $1}'))"
            fi
        fi
    fi
fi

# ── Step 5: code-review-graph (agent + full only) ────────────────────────────

if [ "$OPENMONO_ROLE" != "inference" ]; then
    next_step "Setting up code-review-graph and graphifyy"

    # Verify Python version (3.10+ required for both packages)
    MIN_PYTHON_VERSION="3.10"
    if command -v python3 &>/dev/null; then
        PYTHON_VERSION=$(python3 --version 2>&1 | grep -oE '[0-9]+\.[0-9]+')
        if [ "$(printf '%s\n' "$MIN_PYTHON_VERSION" "$PYTHON_VERSION" | sort -V | head -n 1)" != "$MIN_PYTHON_VERSION" ]; then
            warn "Python version is older than $MIN_PYTHON_VERSION (current: $PYTHON_VERSION)"
            die "Please run install_prereqs_macos.sh first to upgrade Python"
        fi
    else
        die "Python 3 is required but not found. Please run install_prereqs_macos.sh first"
    fi

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

# ── Step 6: Install llama.cpp (inference + full only) ─────────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Installing llama.cpp (Metal)"

    # llama.cpp from Homebrew runs natively on the host, enabling Metal GPU
    # acceleration on Apple Silicon. The agent Docker container reaches it via
    # host.docker.internal, which Docker Desktop resolves automatically on macOS.
    if ! command -v llama-server &>/dev/null; then
        info "Installing llama.cpp via Homebrew..."
        run brew install llama.cpp || die "brew install llama.cpp failed"
    else
        ok "llama-server already installed: $(command -v llama-server)"
    fi

    # Write the full inference config to ~/.openmono/settings.json.
    # This is the single source of truth for all runtime commands (start/stop/status).
    SETTINGS_FILE="$HOME/.openmono/settings.json"
    mkdir -p "$(dirname "$SETTINGS_FILE")"
    [ ! -f "$SETTINGS_FILE" ] && echo '{}' > "$SETTINGS_FILE"

    # Generate a new API key for this install (stored in docker/.env — single source of truth)
    LLAMA_API_KEY="$(openssl rand -hex 24)"
    ENV_FILE="$INSTALL_DIR/docker/.env"
    mkdir -p "$(dirname "$ENV_FILE")"
    if [ ! -f "$ENV_FILE" ]; then
        touch "$ENV_FILE"
    fi
    # Remove existing LLAMA_API_KEY if present, then add the new one
    grep -v '^LLAMA_API_KEY=' "$ENV_FILE" > /tmp/env.tmp 2>/dev/null || true
    echo "LLAMA_API_KEY=$LLAMA_API_KEY" >> /tmp/env.tmp
    mv /tmp/env.tmp "$ENV_FILE"
    chmod 0600 "$ENV_FILE"

    python3 - "$SETTINGS_FILE" \
        "${LLAMA_PORT:-7474}" \
        "$MODEL_NAME" \
        "$MODEL_ALIAS" \
        "$_CTX_SIZE" \
        "$INSTALL_DIR" \
        "$LLAMA_API_KEY" \
        "${MMPROJ_FILE:-}" <<'PYEOF'
import json, sys
path, port, model_name, model_alias, ctx_size, install_dir, api_key, mmproj_path = sys.argv[1:9]
with open(path) as f:
    cfg = json.load(f)
cfg.setdefault("llm", {})["endpoint"] = f"http://host.docker.internal:{port}"
cfg.setdefault("llm", {})["api_key"] = api_key
cfg["inference"] = {
    "mode": "native-metal",
    "model_name": model_name,
    "model_alias": model_alias,
    "ctx_size": int(ctx_size),
    "port": int(port),
    "install_dir": install_dir,
    "mmproj_path": mmproj_path if mmproj_path else ""
}
with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
PYEOF
    ok "Inference config written to: $SETTINGS_FILE"
    detail "  model     : $MODEL_NAME"
    detail "  ctx_size  : $_CTX_SIZE"
    detail "  endpoint  : http://host.docker.internal:${LLAMA_PORT:-7474}"
    detail "  api_key   : [generated, stored in docker/.env]"

    # Update docker/.env with model and vision state
    grep -v -E "^MODEL_MMPROJ=|^OPENMONO_VISION_ENABLED=" "$ENV_FILE" > /tmp/env.tmp 2>/dev/null || true
    mv /tmp/env.tmp "$ENV_FILE"
    printf "MODEL_MMPROJ=%s\n" "${MODEL_MMPROJ:-}" >> "$ENV_FILE"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        printf "OPENMONO_VISION_ENABLED=1\n" >> "$ENV_FILE"
    else
        printf "OPENMONO_VISION_ENABLED=0\n" >> "$ENV_FILE"
    fi
fi

# ── Step 7: Build Docker images (skipped for inference) ──────────────────────

if [ "$OPENMONO_ROLE" != "inference" ]; then
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

    info "Building agent image..."
    if ! run $DOCKER_COMPOSE_CMD build agent; then
        die "agent build failed"
    fi

    ok "Docker images built"
fi

# ── Step 8: Start llama-server (inference + full only) ──────────────────────

if [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Starting llama-server (Metal)"

    # Use openmono start to launch llama-server via scripts/macos/inference.sh
    # This ensures consistency between install and runtime startup paths.
    export LLAMA_PORT="${LLAMA_PORT:-7474}"
    if ! "$INSTALL_DIR/openmono" start; then
        warn "llama-server startup did not complete cleanly"
        warn "Check the log: openmono logs"
        warn "Or restart manually: openmono start"
    fi
fi

# ── Shell integration ─────────────────────────────────────────────────────────
# Shell rc file updates are handled by openmono cmd_setup after installation completes
# This ensures we only update the appropriate files for the user's actual shell

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
printf "${GREEN}${BOLD}  Installation Complete${NC} (role: %s)\n" "$OPENMONO_ROLE"
printf "${GREEN}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
echo ""

case "$OPENMONO_ROLE" in
    full)
        echo "  llama-server port : ${LLAMA_PORT:-7474}"
        echo "  mode              : Metal GPU (native llama.cpp)"
        echo "  model             : ${MODEL_NAME:-}"
        if [ -n "${MODEL_MMPROJ:-}" ]; then
            echo "  vision            : enabled (${MODEL_MMPROJ})"
        fi
        ;;
    inference)
        echo "  llama-server port : ${LLAMA_PORT:-7474}"
        echo "  mode              : Metal GPU (native llama.cpp)"
        echo "  model             : ${MODEL_NAME:-}"
        if [ -n "${MODEL_MMPROJ:-}" ]; then
            echo "  vision            : enabled (${MODEL_MMPROJ})"
        fi
        ;;
    agent)
        echo "  role              : Agent only (dual-box mode)"
        ;;
esac
echo ""
show_log_location

# Vision usage info (if enabled)
if [ "${OPENMONO_ROLE}" != "agent" ] && [ -n "${MODEL_MMPROJ:-}" ]; then
    echo ""
    printf "${BOLD}${CYAN}Vision (LLaVA) — Enabled${NC}\n"
    printf "  ${DIM}Vision encoder (mmproj) loaded with the model (~1-2 GB unified memory)${NC}\n"
    printf "  ${DIM}Context: reduced $((_CTX_ORIG/1024))k → $((_CTX_SIZE/1024))k to fit encoder burst${NC}\n"
    printf "\n"
    printf "  ${DIM}Usage: type @image.png or @screenshot.png in chat${NC}\n"
    printf "  ${DIM}  Examples:${NC}\n"
    printf "     @screenshot.png what's on my screen?\n"
    printf "     @diagram.png explain this diagram\n"
    printf "\n"
    printf "  ${DIM}To disable: delete the mmproj file and re-run openmono setup${NC}\n"
    printf "     rm ~/openmono.ai/models/${MODEL_MMPROJ}${NC}\n"
    echo ""
fi

# ── Done ──────────────────────────────────────────────────────────────────────
# The shell restart and docker group activation is handled by openmono cmd_setup
# so that the post-install guidance is shown before the shell restarts.

# Write environment to the file passed by openmono cmd_setup
if [[ -n "${OPENMONO_ENV_FILE:-}" ]]; then
    cat > "$OPENMONO_ENV_FILE" <<ENVEOF
export INSTALL_DIR="$INSTALL_DIR"
export LLAMA_PORT="${LLAMA_PORT:-7474}"
export OPENMONO_ROLE="$OPENMONO_ROLE"
export MODEL_NAME="${MODEL_NAME:-}"
export MODEL_ACCURACY="${MODEL_ACCURACY:-standard}"
ENVEOF
    _log "Wrote install environment to: $OPENMONO_ENV_FILE"
else
    warn "OPENMONO_ENV_FILE not set (openmono cmd_setup should have set this)"
fi
