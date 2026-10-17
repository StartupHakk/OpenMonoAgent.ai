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
# shellcheck source=lib/prism.sh
source "$SCRIPT_DIR/lib/prism.sh"
# shellcheck source=lib/model-catalog.sh
source "$SCRIPT_DIR/lib/model-catalog.sh"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/lib/vision.sh"

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
    local ram_gb="${1:-0}" key
    # Data-driven: the macos tier ladder (>=48/32/16/else->8) and per-tier
    # ctx/name/url/label live in scripts/models.json. mc_pick_tier mirrors the
    # old >= ladders (largest tier key <= ram_gb, clamped to the smallest).
    key="$(mc_pick_tier qwen macos "$ram_gb")"
    [ -n "$key" ] || key=8
    mc_select qwen macos "$key"
    _CTX_SIZE="$(mc_tier_field qwen macos "$key" ctx)"
}

# Bonsai picker for Apple Silicon. Called AFTER unified-memory detection in
# Step 2 (TOTAL_MEM must be set). Sets IS_BONSAI=1 and overrides
# MODEL_*/_MODEL_LABEL/_CTX_SIZE when Bonsai is chosen.
#
# Tiering (unified memory is shared with the OS, so budget ~6-8GB for macOS):
#   ≥24GB → PQ2_0 (7.2GB weights leave ample room for KV + mmproj)
#   16-23GB → PTQ1_0 only (5.95GB; PQ2_0 would swap-thrash)
# Conservative by design: Qwen is the default on TTY and non-TTY alike until
# the Metal ternary path has a confirmed good run. An explicit --model=bonsai-*
# that doesn't fit the Mac fails here, before any multi-GB download.
prompt_bonsai_macos() {
    local choice="${OPENMONO_MODEL:-auto}" _bonsai_want=""
    case "$choice" in
        bonsai-pq2|bonsai|pq2) _bonsai_want=pq2 ;;
        bonsai-ptq1|ptq1)      _bonsai_want=ptq1 ;;
        qwen) return 0 ;;
        auto) ;;
        *) warn "Unknown --model=$choice — using Qwen"; return 0 ;;
    esac
    if [ -z "$_bonsai_want" ]; then
        # No explicit flag: offer on TTY only, Qwen default. 16GB Macs are
        # offered PTQ1_0 only; PQ2_0 appears from 24GB up.
        if [ ! -t 0 ] || [ "${TOTAL_MEM:-0}" -lt 16 ]; then return 0; fi
        flush_stdin
        echo ""
        echo "  Which model for this Mac? (${TOTAL_MEM}GB unified memory)"
        echo ""
        echo "  1) Qwen (default, tested on Metal)"
        if [ "$TOTAL_MEM" -ge 24 ]; then
        echo "  2) Ternary-Bonsai-2-27B PQ2_0 (~7.2GB) [EXPERIMENTAL on Metal]"
        echo "  3) Ternary-Bonsai-2-27B PTQ1_0 (~5.9GB) [EXPERIMENTAL on Metal]"
        else
        echo "  2) Ternary-Bonsai-2-27B PTQ1_0 (~5.9GB) [EXPERIMENTAL on Metal]"
        fi
        echo ""
        printf "  Enter 1, 2 or 3 [default: 1]: "
        read -r -n 1 _bmc_choice; echo ""
        case "${_bmc_choice:-1}" in
            2) _bonsai_want=$([ "$TOTAL_MEM" -ge 24 ] && echo pq2 || echo ptq1) ;;
            3) _bonsai_want=$([ "$TOTAL_MEM" -ge 24 ] && echo ptq1 || echo "") ;;
            *) return 0 ;;
        esac
        [ -z "$_bonsai_want" ] && return 0
    fi
    if [ "$_bonsai_want" = "pq2" ] && [ "${TOTAL_MEM:-0}" -lt 24 ]; then
        die "Bonsai PQ2_0 on macOS needs ≥24GB unified memory (this Mac: ${TOTAL_MEM}GB). Use --model=bonsai-ptq1 or --model=qwen."
    fi
    if [ "${TOTAL_MEM:-0}" -lt 16 ]; then
        die "Bonsai on macOS needs ≥16GB unified memory (this Mac: ${TOTAL_MEM}GB). Re-run with --model=qwen."
    fi
    select_bonsai_model "$_bonsai_want"
    if [ "$TOTAL_MEM" -ge 48 ]; then
        _CTX_SIZE=98304
    elif [ "$TOTAL_MEM" -ge 24 ]; then
        _CTX_SIZE=65536
    else
        _CTX_SIZE=32768
        warn "16GB Mac + Bonsai: tight fit — expect swap under load; Qwen 9B is the safer pick here"
    fi
    ok "Bonsai selected (EXPERIMENTAL on Metal): $MODEL_NAME (ctx: $_CTX_SIZE)"
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
    IS_BONSAI=0
    select_model "$TOTAL_MEM"
    # Bonsai picker fires here — AFTER unified-memory detection — and is
    # conservative by design (Metal ternary kernels still being validated):
    # Qwen is the default everywhere; Bonsai is opt-in via prompt choice or
    # an explicit --model=bonsai-* flag (PQ2_0 needs ≥24GB, PTQ1_0 ≥16GB).
    prompt_bonsai_macos

    # Vision is a user choice (when the model offers a projector): ask before
    # deciding context size — declining restores the full context. OPENMONO_VISION=
    # yes|no presets it non-interactively.
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        _vision_choice="${OPENMONO_VISION:-}"
        if [ -z "$_vision_choice" ] && [ -t 0 ]; then
            flush_stdin
            echo ""
            echo "  Enable vision (image input)? The projector adds ~900 MB of"
            echo "  weights and ~1-2 GB of unified-memory headroom; it lets the"
            echo "  agent read screenshots and image files (@image.png in chat)."
            printf "  Enable vision? [Y/n]: "
            read -r _vision_answer || _vision_answer=""
            case "${_vision_answer:-Y}" in
                [Nn]*) _vision_choice="no" ;;
                *)     _vision_choice="yes" ;;
            esac
        elif [ -z "$_vision_choice" ]; then
            _vision_choice="yes"   # non-interactive: catalog default (on when available)
        fi
        if [ "$_vision_choice" = "no" ] || [ "$_vision_choice" = "off" ]; then
            MODEL_MMPROJ=""
            MODEL_MMPROJ_URL=""
            info "Vision disabled by choice — full context kept (no mmproj)."
        fi
    fi

    # Reduce context when mmproj is active (encoder needs ~1-2 GB burst).
    # Qwen presets only — Bonsai ctx is already sized conservatively above.
    if [ "${IS_BONSAI:-0}" != "1" ] && [ -n "${MODEL_MMPROJ:-}" ]; then
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
    # acceleration on Apple Silicon. The endpoint is stored as 127.0.0.1
    # (resolves everywhere on the host); every container consumer maps it
    # for its own network — the bridge and the openmono CLI rewrite loopback
    # to host.docker.internal on macOS (Docker Desktop) and to the compose
    # service names on Linux. Never store host.docker.internal here: macOS
    # itself does not resolve it, so host-side readers would break.
    if ! command -v llama-server &>/dev/null; then
        info "Installing llama.cpp via Homebrew..."
        run brew install llama.cpp || die "brew install llama.cpp failed"
    else
        ok "llama-server already installed: $(command -v llama-server)"
    fi

    # Prism fork for Bonsai (Metal ternary kernels) — built on-machine, kept
    # outside the repo so `openmono upgrade` never touches it. The brew stock
    # binary above stays untouched for the Qwen path.
    PRISM_BINARY=""
    if [ "${IS_BONSAI:-0}" = "1" ]; then
        build_prism_macos || die "Prism fork build failed — cannot run Bonsai without it"
        PRISM_BINARY="$(prism_macos_bin)"
        warn "First llama-server start compiles Metal shaders — can take several minutes."
        warn "Don't kill it; watch progress with: openmono logs"
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
        "${MMPROJ_FILE:-}" \
        "${PRISM_BINARY:-}" <<'PYEOF'
import json, sys
path, port, model_name, model_alias, ctx_size, install_dir, api_key, mmproj_path, binary = sys.argv[1:10]
with open(path) as f:
    cfg = json.load(f)
cfg.setdefault("llm", {})["endpoint"] = f"http://127.0.0.1:{port}"
cfg.setdefault("llm", {})["api_key"] = api_key
cfg["inference"] = {
    "mode": "native-metal",
    "model_name": model_name,
    "model_alias": model_alias,
    "ctx_size": int(ctx_size),
    "port": int(port),
    "install_dir": install_dir,
    "mmproj_path": mmproj_path if mmproj_path else "",
    "binary": binary if binary else ""
}
with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
PYEOF
    ok "Inference config written to: $SETTINGS_FILE"
    detail "  model     : $MODEL_NAME"
    detail "  binary    : ${PRISM_BINARY:-$(command -v llama-server)}"
    detail "  ctx_size  : $_CTX_SIZE"
    detail "  endpoint  : http://127.0.0.1:${LLAMA_PORT:-7474}"
    detail "  api_key   : [generated, stored in docker/.env]"

    # Update docker/.env with model and vision state
    grep -v -E "^MODEL_MMPROJ=|^OPENMONO_VISION_ENABLED=" "$ENV_FILE" > /tmp/env.tmp 2>/dev/null || true
    mv /tmp/env.tmp "$ENV_FILE"
    printf "MODEL_MMPROJ=%s\n" "${MODEL_MMPROJ:-}" >> "$ENV_FILE"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        printf "OPENMONO_VISION_ENABLED=1\n" >> "$ENV_FILE"
        om_persist_vision true
    else
        printf "OPENMONO_VISION_ENABLED=0\n" >> "$ENV_FILE"
        om_persist_vision false
    fi
fi

# ── Step 7: Build Docker images ──────────────────────────────────────────────

# Inference-only boxes don't need the agent image — ask instead of silently
# skipping. Non-interactive defaults to No (preset OPENMONO_BUILD_AGENT=1).
_build_agent=1
if [ "$OPENMONO_ROLE" = "inference" ]; then
    _build_agent="${OPENMONO_BUILD_AGENT:-}"
    if [ -z "$_build_agent" ]; then
        if [ ! -t 0 ]; then
            _build_agent=0
        else
            echo ""
            printf "  Build the agent Docker image on this inference box? Default No [y/N]: "
            read -r _ba || _ba=""
            echo ""
            case "${_ba:-}" in
                [Yy]|[Yy]es) _build_agent=1 ;;
                *) _build_agent=0 ;;
            esac
        fi
    fi
fi

if [ "$OPENMONO_ROLE" != "inference" ] || [ "$_build_agent" = "1" ]; then
    # The inference role only counts this step when the build is actually run.
    [ "$OPENMONO_ROLE" = "inference" ] && TOTAL_STEPS=7
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
else
    detail "Skipping agent image (inference-only box). Add later: docker compose build agent"
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

# ── Optional: bare-metal host sub-agent (agent roles only — never inference)
# Mirrors the Linux installer: prompt (TTY only; non-interactive defaults to
# No), then host-command policy, then sudo policy, then install-host-bridge.sh
# with the answers as non-interactive presets. Deliberately outside the step
# counter so TOTAL_STEPS stays valid for every role.
if [ "$OPENMONO_ROLE" != "inference" ]; then
    _bridge_answer="${OPENMONO_HOST_BRIDGE:-}"
    if [[ -z "$_bridge_answer" ]]; then
        if [[ ! -t 0 ]]; then
            info "Skipping host sub-agent (non-interactive, default No). Add it later with:"
            info "  OPENMONO_HOST_BRIDGE=1 bash scripts/install_macos.sh  # or: bash scripts/install-host-bridge.sh"
        else
            echo ""
            echo "  ── OPTIONAL: bare-metal host operator (DEFAULT: No) ──"
            echo "  This BREAKS the container sandbox. If installed, the agent can"
            echo "  run HOST commands directly on THIS MAC as your user (pull, build,"
            echo "  install, docker, launchctl, redeploy, troubleshoot). Every command"
            echo "  is audit-logged, sudo stays OFF unless you separately opt in."
            echo "  Only install on a Mac you operate. Coding laptops: say No."
            echo "  You can add it later with: bash scripts/install-host-bridge.sh"
            echo ""
            printf "  Install the host sub-agent? Default No [y/N] (Enter=No): "
            read -r _bridge_answer || _bridge_answer=""
            echo ""
        fi
    fi
    case "${_bridge_answer:-}" in
        1|[Yy]|[Yy]es)
            # Second explicit consent: "allow" must be chosen, never assumed.
            # OPENMONO_HOST_EXEC_DEFAULT=ask|allow presets it non-interactively.
            _bridge_policy="${OPENMONO_HOST_EXEC_DEFAULT:-}"
            if [[ -z "$_bridge_policy" && -t 0 ]]; then
                echo "  Host commands run as your user. Choose how to confirm them:"
                echo "    1) Ask every time (recommended) — prompt [y/N] per host command"
                echo "    2) Allow routine without asking — git/docker/status run immediately"
                echo "       (deny list + sudo rules + audit log still apply)"
                echo ""
                printf "  Host command policy? [1/2] (Default 1=Ask): "
                read -r _bridge_policy_answer || _bridge_policy_answer=""
                echo ""
                case "${_bridge_policy_answer:-}" in
                    2|[Aa]llow) _bridge_policy="allow" ;;
                    *) _bridge_policy="ask" ;;
                esac
            fi
            # Fail closed: anything but an explicit "allow" becomes "ask".
            case "${_bridge_policy:-}" in
                [Aa]llow) export OPENMONO_HOST_EXEC_DEFAULT="allow" ;;
                *) export OPENMONO_HOST_EXEC_DEFAULT="ask" ;;
            esac
            # Sudo policy preset (never silent): OPENMONO_HOST_SUDO presets
            # it non-interactively. When unset and on a TTY, ask; fail
            # closed to the default (ask) otherwise. install-host-bridge.sh
            # turns this into the --allow-sudo / --no-sudo --init flag.
            _bridge_sudo="${OPENMONO_HOST_SUDO:-}"
            if [[ -z "$_bridge_sudo" && -t 0 ]]; then
                echo "  The agent may need sudo for host commands that require it"
                echo "  (service restarts, package installs, log reads). Note: Touch ID"
                echo "  does not satisfy bridge sudo — the typed password is required."
                echo "  Choose how to handle privilege escalation:"
                echo "    1) Ask (recommended) — confirm each escalation with [y/N];"
                echo "       non-interactive sessions fail closed to No"
                echo "    2) Allow — run escalations without confirming (still audited)"
                echo "    3) Deny — never escalate; host commands run as your user only"
                echo ""
                printf "  Sudo policy? [1/2/3] (Default 1=Ask): "
                read -r _bridge_sudo_answer || _bridge_sudo_answer=""
                echo ""
                case "${_bridge_sudo_answer:-}" in
                    2|[Aa]llow|[Yy]|[Yy]es) _bridge_sudo="1" ;;
                    3|[Dd]eny|[Nn]|[Nn]o)  _bridge_sudo="0" ;;
                    *) _bridge_sudo="" ;;   # default: ask
                esac
            fi
            case "${_bridge_sudo:-}" in
                1|[Yy]|[Yy]es) export OPENMONO_HOST_SUDO="1" ;;
                0|[Nn]|[Nn]o)  export OPENMONO_HOST_SUDO="0" ;;
                *) : ;;   # leave unset → bridge defaults to "ask"
            esac
            info "Installing host sub-agent (host-exec default: $OPENMONO_HOST_EXEC_DEFAULT)..."
            if bash "$SCRIPT_DIR/install-host-bridge.sh"; then
                ok "Host sub-agent installed"
                export OPENMONO_HOST_BRIDGE=1
                case "${OPENMONO_HOST_SUDO:-}" in
                    1|[Yy]|[Yy]es)
                        info "Sudo: allow — privilege escalations run without confirming (still audited)." ;;
                    0|[Nn]|[Nn]o)
                        info "Sudo: deny — host commands run as your user only; escalation is refused." ;;
                    *)
                        info "Sudo: ask — each escalation is confirmed [y/N]; non-interactive sessions fail closed to No." ;;
                esac
                info "First run of 'openmono agent --host' confirms the run-as user."
                info "Change later with: openmono config set host.sudo_policy ask|allow|deny"
                info "Host settings: openmono config set host.exec_default|host.run_as|host.timeout_ms <value>"
            else
                warn "Host sub-agent install failed — container agent still works; retry: bash scripts/install-host-bridge.sh"
                export OPENMONO_HOST_BRIDGE=0
            fi
            ;;
    esac
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
    # Bonsai sizes ctx conservatively up front — no reduction step ran, so
    # _CTX_ORIG only exists on the Qwen path.
    if [ -n "${_CTX_ORIG:-}" ] && [ "${_CTX_ORIG}" != "${_CTX_SIZE}" ]; then
        printf "  ${DIM}Context: reduced $((_CTX_ORIG/1024))k → $((_CTX_SIZE/1024))k to fit encoder burst${NC}\n"
    else
        printf "  ${DIM}Context: $_CTX_SIZE tokens (unchanged — sized for the encoder burst)${NC}\n"
    fi
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

# ── Vision note (agent role) ────────────────────────────────────────────
# Whether the remote endpoint actually serves images is detected at startup
# from the server's /props capabilities (modalities.vision) — no install-time
# guess to go stale. Optional manual override:
#   openmono config set inference.vision_enabled true|false

# ── Done ──────────────────────────────────────────────────────────────────────
# The shell restart and docker group activation is handled by openmono cmd_setup
# so that the post-install guidance is shown before the shell restarts.

# Write environment to the file passed by openmono cmd_setup
if [[ -n "${OPENMONO_ENV_FILE:-}" ]]; then
    cat > "$OPENMONO_ENV_FILE" <<ENVEOF
export INSTALL_DIR="$INSTALL_DIR"
export LLAMA_PORT="${LLAMA_PORT:-7474}"
export OPENMONO_ROLE="$OPENMONO_ROLE"
export OPENMONO_HOST_BRIDGE="${OPENMONO_HOST_BRIDGE:-0}"
export MODEL_NAME="${MODEL_NAME:-}"
export MODEL_ACCURACY="${MODEL_ACCURACY:-standard}"
ENVEOF
    _log "Wrote install environment to: $OPENMONO_ENV_FILE"
else
    warn "OPENMONO_ENV_FILE not set (openmono cmd_setup should have set this)"
fi
