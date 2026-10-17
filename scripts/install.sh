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
#                         inference = GPU box only: model + inference server, no agent tooling
#                         agent     = laptop only: agent + code-review-graph, no model
#   OPENMONO_INFERENCE_BACKEND
#                         Inference engine on Linux: llama (default) or strata.
#                         llama  = standard bundled llama.cpp Docker path
#                                  (Qwen stock; Bonsai adds the Prism image).
#                         strata = Strata (Qwen3.8-Flash-Next, OpenAI-compatible
#                                  :8080/v1) via scripts/strata.sh — no vendoring,
#                                  needs Linux + NVIDIA + CUDA 13 (auto-installed),
#                                  see docs/STRATA.md. Opt in with --model=flash-next.
#                         (macOS Apple Silicon always uses its native path.)
#   OPENMONO_GPU=1        Force GPU mode (writes GPU docker-compose override)
#   OPENMONO_CPU=1        Force CPU mode (removes any GPU override)
#   OPENMONO_VERBOSE=1    Show detailed command output
#   LLAMA_PORT=7474       llama-server host port (default 7474)
#   OPENMONO_MODEL        Model engine: auto (default), qwen, bonsai-pq2, bonsai-ptq1, flash-next
#                         auto = analyze hardware, then pick (default Qwen on llama.cpp;
#                                Bonsai/Strata offered only when the box can run them)
#   OPENMONO_PRISM_IMAGE_CUDA   Prism CUDA image ref (default startuphakk/prism-llama.cpp_cuda:latest)
#   OPENMONO_PRISM_IMAGE_VULKAN Prism Vulkan image ref (default startuphakk/prism-llama.cpp_vulkan:latest)
#   OPENMONO_PRISM_IMAGE        Legacy single-ref override (wins over both when set)
#   OPENMONO_PRISM_FORCE_PULL=1 Re-pull the Prism image even if already present
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
# shellcheck source=lib/prism.sh
source "$SCRIPT_DIR/lib/prism.sh"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/lib/model-catalog.sh"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/lib/vision.sh"

# ── Context size presets (tokens) — edit here to change globally ─────────────
CTX_24G=196608       # GPU 24GB+  q8_0 KV  (~8GB KV + 15GB weights ≈ 23GB)
CTX_16G=180224       # GPU 16GB   q4_0 KV  (~3.7GB KV + 12GB weights ≈ 16GB)
CTX_12G=196608       # GPU 12GB   q4_0 KV  (9B weights ~5GB, fits)
CTX_CPU=196608       # CPU/Vulkan q8_0 KV
CTX_VISION=172032    # 24GB+ tier + mmproj — 723 MiB free at rest; vision encoder needs ~474 MiB burst
CTX_VISION_16G=98304  # 16GB tier + mmproj — ~1GB less KV than 128k, gives vision encoder burst headroom

# MODEL_ACCURACY, MODEL_ALIAS, and _MODEL_LABEL.
# Data-driven: the per-tier Qwen rows live in scripts/models.json and are
# resolved by lib/model-catalog.sh. _GPU_TIER maps to a linux tier key
# (24/16/12, everything else -> cpu) exactly as the old case table did.
select_model() {
    local key
    case "${1:-0}" in
        24|16|12) key="${1:-0}" ;;
        *)        key="cpu" ;;
    esac
    mc_select qwen linux "$key"
}

# (Bonsai model selection lives in scripts/lib/prism.sh as
# select_bonsai_model(), shared with the macOS installer.)

# Unified model + engine picker. ALWAYS defaults to Qwen on stock llama.cpp —
# the same default as main. Bonsai (Prism CUDA/Vulkan) and Qwen3.8-Flash-Next
# (Strata, NVIDIA/CUDA 13) are offered ONLY when the caller's CAN_BONSAI /
# CAN_STRATA capability flags say this box can run them. Honors OPENMONO_MODEL
# (auto|qwen|bonsai-pq2|bonsai-ptq1|flash-next) and an explicit
# OPENMONO_INFERENCE_BACKEND=strata. Sets INFERENCE_BACKEND; for llama choices
# also MODEL_* / IS_BONSAI (via select_bonsai_model). Requires _GPU_TIER,
# GPU_MODE/AMD_IGPU_MODE and CAN_BONSAI/CAN_STRATA set by the caller.
prompt_model_choice() {
    IS_BONSAI=0
    local choice="${OPENMONO_MODEL:-auto}"
    local explicit_backend="${OPENMONO_INFERENCE_BACKEND:-}"

    # Explicit Strata opt-in (by model name or backend) — validated up front so
    # nobody is offered a ~70GB install their box can't run.
    if [ "$choice" = "flash-next" ] || [ "$explicit_backend" = "strata" ]; then
        if [ "$(mc_flag "$(mc_requires flash-next)")" != "1" ]; then
            die "$(mc_deny flash-next)"
        fi
        INFERENCE_BACKEND=strata
        return 0
    fi

    # Explicit llama-family selection: honor it (Bonsai hardware re-checked later).
    case "$choice" in
        bonsai-pq2|bonsai|pq2) select_bonsai_model pq2; INFERENCE_BACKEND=llama; return 0 ;;
        bonsai-ptq1|ptq1)      select_bonsai_model ptq1; INFERENCE_BACKEND=llama; return 0 ;;
        qwen)                  INFERENCE_BACKEND=llama; return 0 ;;
    esac

    # choice == auto. Non-interactive: safe default = Qwen on stock llama.cpp.
    if [ ! -t 0 ]; then
        INFERENCE_BACKEND=llama
        info "Model: Qwen on stock llama.cpp (default). For another engine: --model=flash-next (Strata, NVIDIA) or --model=bonsai-pq2|bonsai-ptq1."
        return 0
    fi

    # Interactive: show only options this box can run; default = Qwen (1).
    flush_stdin
    echo ""
    echo "  Which model + engine for this box? (GPU tier: ${_GPU_TIER:-CPU})"
    echo ""
    printf '  1) %s\n' "$(mc_menu qwen line)"
    local bonsai_pq2=0 bonsai_ptq1=0 strata_n=0 n=1 _ln
    _ln="$(mc_menu bonsai-pq2 line)"
    if [ -n "$_ln" ] && [ "$(mc_flag "$(mc_requires bonsai-pq2)")" = "1" ]; then
        bonsai_pq2=$(( n + 1 ));  printf '  %s) %s\n' "$bonsai_pq2"  "$_ln"
        _ln="$(mc_menu bonsai-ptq1 line)"
        bonsai_ptq1=$(( n + 2 )); printf '  %s) %s\n' "$bonsai_ptq1" "$_ln"
        n=$(( n + 2 ))
    fi
    _ln="$(mc_menu flash-next line)"
    if [ -n "$_ln" ] && [ "$(mc_flag "$(mc_requires flash-next)")" = "1" ]; then
        strata_n=$(( n + 1 )); printf '  %s) %s\n' "$strata_n" "$_ln"
    fi
    echo ""
    printf "  Enter choice [default: 1]: "
    read -r _mc; echo ""
    _mc="${_mc:-1}"
    if [ "$strata_n" != "0" ] && [ "$_mc" = "$strata_n" ]; then
        INFERENCE_BACKEND=strata
        return 0
    fi
    if [ "$bonsai_pq2" != "0" ] && [ "$_mc" = "$bonsai_pq2" ]; then
        select_bonsai_model pq2; INFERENCE_BACKEND=llama; return 0
    fi
    if [ "$bonsai_ptq1" != "0" ] && [ "$_mc" = "$bonsai_ptq1" ]; then
        select_bonsai_model ptq1; INFERENCE_BACKEND=llama; return 0
    fi
    # anything else (incl. bare Enter) → default Qwen on stock llama.cpp
    INFERENCE_BACKEND=llama
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

# ── Inference backend (Linux only; macOS uses its native path) ───────────────
# The backend is FINALIZED by the unified picker further below (analyze the
# box → present only runnable options → default Qwen on stock llama.cpp). The
# default here is llama to match main; only an explicit
# OPENMONO_INFERENCE_BACKEND=strata short-circuits ahead of the picker.
# Agent role needs no local engine, so the backend is informational there.
if [ "$OPENMONO_ROLE" != "agent" ]; then
    INFERENCE_BACKEND="${OPENMONO_INFERENCE_BACKEND:-llama}"
else
    INFERENCE_BACKEND="${OPENMONO_INFERENCE_BACKEND:-none}"
fi
case "$INFERENCE_BACKEND" in
    strata|llama|none) ;;
    *) echo "ERROR: Invalid OPENMONO_INFERENCE_BACKEND='$INFERENCE_BACKEND' (expected: strata, llama)" >&2; exit 1 ;;
esac
if [ "${OPENMONO_INFERENCE_BACKEND:-}" = "strata" ]; then
    info "Inference backend: Strata (Qwen3.8-Flash-Next) — explicitly selected via OPENMONO_INFERENCE_BACKEND"
    info "Docs: docs/STRATA.md — Strata is MIT-licensed upstream (github.com/Niko1221/Strata)"
fi

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
IS_BONSAI=0
CAN_BONSAI=0
CAN_STRATA=0
# Analyze the box, keep only the options it can actually run, then pick
# (default Qwen on stock llama.cpp). Sets INFERENCE_BACKEND + MODEL_* that every
# downstream step (download, docker env, start) reads. Runs for EVERY
# inference-capable role, regardless of backend.
if [ "$OPENMONO_ROLE" != "agent" ]; then
    if [ "${GPU_MODE:-0}" = "1" ]; then
        if command -v nvidia-smi &>/dev/null; then
            _VRAM_MB=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | awk 'NR==1{print $1}')
            _VRAM_MB=${_VRAM_MB:-0}
            if   [ "$_VRAM_MB" -ge 24000 ]; then _GPU_TIER=24; ok "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — full accuracy tier (24GB+)"
            elif [ "$_VRAM_MB" -ge 16000 ]; then _GPU_TIER=16; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — lower accuracy tier (16GB). For best results use 24GB+ VRAM."
            elif [ "$_VRAM_MB" -ge 12000 ]; then _GPU_TIER=12; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — lower accuracy tier (12GB). For best results use 24GB+ VRAM."
            elif [ "$_VRAM_MB" -ge 8000 ]; then _GPU_TIER=8; warn "GPU VRAM: $(( (_VRAM_MB + 512) / 1024 ))GB — 8GB tier (Bonsai PTQ1_0 only, small context). For best results use 24GB+ VRAM."
            else
                warn "GPU mode selected but only $(( (_VRAM_MB + 512) / 1024 ))GB VRAM — minimum is 8GB. Falling back to CPU mode."
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
    # Which options can THIS box run? Bonsai needs an accelerated llama.cpp
    # (Prism CUDA or Vulkan) — there is no CPU image. Strata (Qwen3.8-Flash-Next)
    # is Linux-only, NVIDIA + CUDA 13; scripts/strata.sh auto-installs the
    # toolkit, so the presence of an NVIDIA GPU is enough to OFFER it (the
    # install step verifies CUDA). CPU-only and AMD/Vulkan-only boxes never see
    # a model or engine they cannot actually run.
    if [ "${GPU_MODE:-0}" = "1" ] || [ "${AMD_IGPU_MODE:-0}" = "1" ]; then
        CAN_BONSAI=1
    fi
    _has_nvidia_hw=false
    if command -v lspci &>/dev/null && lspci 2>/dev/null | grep -qi 'nvidia'; then _has_nvidia_hw=true; fi
    if [ -e /proc/driver/nvidia ] || command -v nvidia-smi &>/dev/null; then _has_nvidia_hw=true; fi
    if [ "$(uname -s)" = "Linux" ] && [ "$_has_nvidia_hw" = true ] && [ "${AMD_IGPU_MODE:-0}" != "1" ]; then
        CAN_STRATA=1
    fi
    # The Flash-Next weights are ~70GB: they stream from disk alongside the
    # engine, so a 48GB-class box runs them (slow first boot, then the KV cache
    # and OS compete for the rest). Gate on system RAM so smaller machines
    # never see the option (48GB-class boxes report ~46-47 GiB in /proc/meminfo
    # once firmware/kernel reservations are subtracted; 32GB boxes report ~31).
    if [ "$CAN_STRATA" = "1" ]; then
        _MEM_GB=$(awk '/^MemTotal:/{printf "%d", $2/1048576}' /proc/meminfo 2>/dev/null || echo 0)
        if [ "${_MEM_GB:-0}" -lt 44 ]; then
            CAN_STRATA=0
            info "RAM: ${_MEM_GB}GB — Strata (Qwen3.8-Flash-Next, ~70GB weights) not offered; it needs a 48GB-class box."
        fi
    fi
    # Unified picker: defaults to Qwen on stock llama.cpp; Bonsai / Strata are
    # offered only when this box supports them. Sets INFERENCE_BACKEND.
    prompt_model_choice
    # Bonsai / Prism model plumbing below applies only to the llama.cpp backend;
    # Strata owns its own weights and engine, so skip it entirely for strata.
    if [ "$INFERENCE_BACKEND" != "strata" ]; then
    # Bonsai gate: needs an accelerated backend (NVIDIA/CUDA or 7940HS/Vulkan).
    # Fail BEFORE the multi-GB model download, not after it.
    if [ "${IS_BONSAI:-0}" = "1" ] && [ "${GPU_MODE:-0}" != "1" ] && [ "${AMD_IGPU_MODE:-0}" != "1" ]; then
        die "Bonsai requires a GPU — CUDA: $(prism_image_cuda) / Vulkan: $(prism_image_vulkan). No CPU image exists. Re-run with --model=qwen for this box."
    fi
    # Early Docker Hub pull: verify the Prism image BEFORE the model download.
    # Explicit --model=bonsai-* + unreachable registry = hard fail (user asked
    # for it). Auto/prompt-selected Bonsai + unreachable registry = fall back
    # to stock Qwen so setup still yields a working box.
    # On the AMD iGPU path without an explicit bonsai flag, stay on stock
    # Qwen (Vulkan Bonsai support is new) — only use the Vulkan Prism image
    # when Bonsai was explicitly requested or already selected on NVIDIA.
    if [ "${IS_BONSAI:-0}" = "1" ]; then
        _PRISM_BACKEND="$(prism_backend)"
        if ! pull_prism_image "$_PRISM_BACKEND"; then
            case "${OPENMONO_MODEL:-auto}" in
                bonsai-pq2|bonsai|pq2|bonsai-ptq1|ptq1)
                    die "Prism image pull failed and --model=${OPENMONO_MODEL} was explicitly requested. Check network/Docker Hub, or re-run with --model=qwen." ;;
                *)
                    warn "Prism image unreachable — falling back to stock Qwen (no Prism image needed)"
                    IS_BONSAI=0
                    _FALLBACK_TIER="$_GPU_TIER"
                    [ "${_FALLBACK_TIER:-0}" -lt 12 ] && _FALLBACK_TIER=12
                    select_model "$_FALLBACK_TIER" ;;
            esac
        fi
    fi
    # 8GB VRAM: vision projector stays resident (~0.63GB + burst) — that plus
    # 5.95GB weights leaves no headroom. Disable it; text inference only.
    if [ "${IS_BONSAI:-0}" = "1" ] && [ "${_GPU_TIER:-0}" -le 8 ]; then
        MODEL_MMPROJ=""
        MODEL_MMPROJ_URL=""
        warn "8GB VRAM: vision (mmproj) disabled to protect VRAM headroom."
        warn "Re-enable later with an 8GB+ headroom check: download $PRISM_MMPROJ_NAME into models/ and set MODEL_MMPROJ in docker/.env"
    fi
    # Vision is a user choice (when the hardware+model allow it): ask instead
    # of silently enabling/disabling. OPENMONO_VISION=yes|no presets it
    # non-interactively; the catalog's mmproj availability still gates whether
    # the question is even possible.
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        _vision_choice="${OPENMONO_VISION:-}"
        if [ -z "$_vision_choice" ] && [ -t 0 ]; then
            flush_stdin
            echo ""
            echo "  Enable vision (image input)? The projector adds ~900 MB of"
            echo "  weights and ~1-2 GB of VRAM/RAM headroom; it lets the agent"
            echo "  read screenshots and image files (@image.png in chat)."
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
            info "Vision disabled by choice — full context restored (no mmproj)."
        fi
    fi
    ok "Model selected: $MODEL_NAME"
    if [ -n "${MODEL_MMPROJ:-}" ]; then
        detail "Vision projector: $MODEL_MMPROJ"
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
# Strata backend: model fetch is owned by Strata's own installer
# (scripts/strata.sh install → ./setup.sh), so there is nothing to do here.

if [ "$INFERENCE_BACKEND" = "strata" ] && [ "$OPENMONO_ROLE" != "agent" ]; then
    next_step "Strata model setup (delegated to Strata installer)"
    info "Strata downloads its own Qwen3.8-Flash-Next weights (~70 GB, resumable)."
    _strata_flags=()
    if [[ ! -t 0 ]]; then
        _strata_flags+=(--yes)
        info "Non-interactive shell — passing --yes (recommended size for this box)."
    fi
    if ! STRATA_YES="$([[ ! -t 0 ]] && echo 1 || echo 0)" bash "$SCRIPT_DIR/strata.sh" install "${_strata_flags[@]}"; then
        die "Strata install failed — retry: bash scripts/strata.sh install (see docs/STRATA.md). Strata's own output: ~/.openmono/strata-setup.log"
    fi
    ok "Strata model ready (server starts in the final step)"
fi

if [ "$OPENMONO_ROLE" != "agent" ] && [ "$INFERENCE_BACKEND" != "strata" ]; then
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
# Strata backend: no llama-server override needed — Strata auto-detects the GPU
# (NVIDIA CUDA / AMD HIP) during its own setup. We only record the backend.

# Defined unconditionally: every backend's start step below reads/writes it
# (Strata and llama alike), so it must not live inside a backend-specific
# branch — that caused "DOCKER_ENV_FILE: unbound variable" on strata installs.
DOCKER_ENV_FILE="$INSTALL_DIR/docker/.env"

if [ "$OPENMONO_ROLE" != "agent" ] && [ "$INFERENCE_BACKEND" = "strata" ]; then
    next_step "Recording Strata backend configuration"

    OVERRIDE_FILE="$INSTALL_DIR/docker/docker-compose.override.yml"
    if [ -f "$OVERRIDE_FILE" ]; then
        info "Removing stale llama-server override ($OVERRIDE_FILE) — Strata serves on the host."
        rm -f "$OVERRIDE_FILE"
    fi
    # Record the backend + port so openmono, healthcheck, and the tunnel script
    # route to Strata instead of llama.
    if [ -f "$DOCKER_ENV_FILE" ]; then
        grep -v -E "^INFERENCE_BACKEND=|^STRATA_PORT=|^OPENMONO_VISION_ENABLED=" "$DOCKER_ENV_FILE" > "${DOCKER_ENV_FILE}.tmp" || true
        mv "${DOCKER_ENV_FILE}.tmp" "$DOCKER_ENV_FILE"
    fi
    printf "INFERENCE_BACKEND=strata\nSTRATA_PORT=%s\n" "${STRATA_PORT:-8080}" >> "$DOCKER_ENV_FILE"
    # Vision was chosen during Strata setup (yes|cpu|no), recorded by strata.sh
    # in ~/.openmono/strata.env; mirror it into settings.json so the agent
    # accepts images through this backend.
    _strata_vision="no"
    [[ -f "$HOME/.openmono/strata.env" ]] && _strata_vision="$(bash -c 'source "$HOME/.openmono/strata.env" 2>/dev/null; printf "%s" "${STRATA_VISION:-no}"')"
    if [ "$_strata_vision" != "no" ]; then
        om_persist_vision true
        printf "OPENMONO_VISION_ENABLED=1\n" >> "$DOCKER_ENV_FILE"
    else
        om_persist_vision false
        printf "OPENMONO_VISION_ENABLED=0\n" >> "$DOCKER_ENV_FILE"
    fi
    detail "Vision: $_strata_vision (mirrored to inference.vision_enabled)"
    detail "Persisted INFERENCE_BACKEND=strata STRATA_PORT=${STRATA_PORT:-8080} to $DOCKER_ENV_FILE"
    detail "Strata endpoint: http://127.0.0.1:${STRATA_PORT:-8080}/v1 (host-native, OpenAI-compatible)"
    detail "Caddy gateway (optional) can front it via docker/docker-compose.strata.yml"
fi

if [ "$OPENMONO_ROLE" != "agent" ] && [ "$INFERENCE_BACKEND" != "strata" ]; then
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
        # Bonsai path: prebuilt Prism CUDA image from Docker Hub (stock llama.cpp rejects PQ2_0/PTQ1_0).
        _LLAMA_IMAGE="ghcr.io/ggml-org/llama.cpp:server-cuda"
        if [ "${IS_BONSAI:-0}" = "1" ]; then
            pull_prism_image cuda || die "Prism image pull failed — cannot run Bonsai without it"
            _LLAMA_IMAGE="$(prism_image_ref cuda)"
            # Bonsai VRAM budget: weights (7.2/5.9GB) + KV. Keep ctx conservative;
            # hybrid-attention backbone keeps long ctx practical, but KV is what OOMs.
            if [ "${_GPU_TIER:-0}" -ge 24 ]; then
                _KV_K="q8_0"; _KV_V="q8_0"
                _CTX=$CTX_BONSAI_24G
            elif [ "${_GPU_TIER:-0}" -ge 16 ]; then
                _KV_K="q4_0"; _KV_V="q4_0"
                _CTX=$CTX_BONSAI_16G
            elif [ "${_GPU_TIER:-0}" -ge 12 ]; then
                _KV_K="q4_0"; _KV_V="q4_0"
                _CTX=$CTX_BONSAI_12G
            else
                _KV_K="q4_0"; _KV_V="q4_0"
                _CTX=$CTX_BONSAI_8G
                warn "8GB VRAM: Bonsai PTQ1_0 with ctx $_CTX — tight fit, keep prompts modest"
            fi
        elif [ "$_GPU_TIER" -ge 24 ]; then
            # 24GB tier: q8 kv cache (high quality); 16GB/12GB tiers: q4 kv cache (saves VRAM)
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
        # Skipped when the base preset already fits vision (e.g. 12G @ 96k ≈ 9.6GB).
        if [ -n "${MODEL_MMPROJ:-}" ]; then
            _CTX_ORIG=$_CTX
            if [ "$_GPU_TIER" -ge 24 ]; then
                _CTX=$CTX_VISION
            elif [ "$_CTX" -gt "$CTX_VISION_16G" ]; then
                _CTX=$CTX_VISION_16G
            fi
            if [ "$_CTX" -ne "$_CTX_ORIG" ]; then
                detail "Vision enabled: context reduced from $(( _CTX_ORIG / 1024 ))k → $(( _CTX / 1024 ))k to fit mmproj in VRAM"
            else
                detail "Vision enabled: context kept at $(( _CTX / 1024 ))k (fits alongside mmproj)"
            fi
        fi
        [ "$MODEL_ACCURACY" = "lower" ] && info "Lower accuracy model selected — q4 kv cache enabled to fit $(( (_VRAM_MB + 512) / 1024 ))GB VRAM"
        # Append mmproj flag inline with --metrics; empty string when no projector configured.
        MMPROJ_OPT="${MODEL_MMPROJ:+--mmproj /models/${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}"
        info "Writing GPU override: $OVERRIDE_FILE"
        cat > "$OVERRIDE_FILE" <<EOF
# GPU/Bonsai configuration (auto-generated by install.sh — ${MODEL_ACCURACY:-full} accuracy)
# Runtime knobs resolve from docker/.env (installer defaults after each :-)
# so tuning never needs a re-run: edit docker/.env, then
# `docker compose up -d llama-server`.
services:
  llama-server:
    image: \${LLAMA_IMAGE:-$_LLAMA_IMAGE}
    command: >
      --model /models/\${MODEL_NAME}
      --alias \${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size \${CTX_SIZE:-$_CTX}
      --threads \${LLAMA_THREADS:-14}
      --n-gpu-layers \${N_GPU_LAYERS:-99}
      --flash-attn \${LLAMA_FLASH_ATTN:-on}
      --cache-type-k \${KV_CACHE_K:-$_KV_K}
      --cache-type-v \${KV_CACHE_V:-$_KV_V}
      --batch-size \${LLAMA_BATCH:-2048}
      --ubatch-size \${LLAMA_UBATCH:-1024}
      --parallel \${LLAMA_PARALLEL:-1}
      --jinja
      --reasoning off
      --metrics \${MODEL_MMPROJ:+--mmproj /models/\${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}
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

    printf "\n  ${BOLD}${CYAN}Tuning knobs (docker/.env, then: docker compose up -d llama-server)${NC}\n"
    printf "     ${DIM}CTX_SIZE=N        halve to free ~half the KV-cache VRAM${NC}\n"
    printf "     ${DIM}                  presets: 32k=32768  64k=65536  96k=98304  128k=131072  192k=196608${NC}\n"
    printf "     ${DIM}KV_CACHE_K/V      f16 (best) → q8_0 → q5_1 → q4_1 → q4_0 (least VRAM)${NC}\n"
    printf "     ${DIM}N_GPU_LAYERS      99=all on GPU; lower to spill layers to CPU RAM${NC}\n"
    printf "     ${DIM}LLAMA_PARALLEL=N  N concurrent slots, each costs ctx-size of KV cache${NC}\n"
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

    # Bonsai on the 7940HS path uses the Prism Vulkan image from Docker Hub;
    # stock Qwen keeps the upstream Vulkan image.
    _VULKAN_IMAGE="ghcr.io/ggml-org/llama.cpp:server-vulkan-b9070"
    if [ "${IS_BONSAI:-0}" = "1" ]; then
        pull_prism_image vulkan || die "Prism Vulkan image pull failed — cannot run Bonsai without it"
        _VULKAN_IMAGE="$(prism_image_ref vulkan)"
    fi

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
# Runtime knobs resolve from docker/.env (installer defaults after each :-)
# so tuning never needs a re-run: edit docker/.env, then
# `docker compose up -d llama-server`.
# NOTE: models volume + host port mapping come from the base compose file.
services:
  llama-server:
    image: \${LLAMA_IMAGE:-$_VULKAN_IMAGE}
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "44"
      - "109"
    shm_size: "4gb"
    environment:
      - GGML_VULKAN_DEVICE=0
      - RADV_PERFTEST=compute
    command: >
      --model /models/\${MODEL_NAME}
      --alias \${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size \${CTX_SIZE:-$_CTX}
      --no-mmap
      --threads \${LLAMA_THREADS:-8}
      --threads-batch \${LLAMA_THREADS:-8}
      --batch-size \${LLAMA_BATCH:-512}
      --ubatch-size \${LLAMA_UBATCH:-256}
      -ngl \${N_GPU_LAYERS:-99}
      --n-gpu-layers \${N_GPU_LAYERS:-99}
      --flash-attn \${LLAMA_FLASH_ATTN:-on}
      --cache-type-k \${KV_CACHE_K:-q8_0}
      --cache-type-v \${KV_CACHE_V:-q8_0}
      --parallel \${LLAMA_PARALLEL:-1}
      --jinja
      --reasoning off
      --metrics \${MODEL_MMPROJ:+--mmproj /models/\${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
EOF
    ok "AMD iGPU override written"
    printf "     ${DIM}model: $MODEL_NAME  ·  ctx: $(( _CTX / 1024 ))k  ·  kv: q8_0  ·  gpu-layers: 99  ·  vision: ${MODEL_MMPROJ:-disabled}${NC}\n"
    printf "     ${DIM}config: $OVERRIDE_FILE  (restart: docker compose up -d llama-server)${NC}\n"

    printf "\n  ${BOLD}${CYAN}Tuning knobs (docker/.env, then: docker compose up -d llama-server)${NC}\n"
    printf "     ${DIM}CTX_SIZE=N        up to 196608 (GTT limits: ~28GB system RAM as VRAM)${NC}\n"
    printf "     ${DIM}KV_CACHE_K/V      f16 (best) → q8_0 → q5_1 → q4_1 → q4_0 (least RAM)${NC}\n"
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
    # Bonsai is gated to CUDA/Vulkan in Step 2 — reaching here means a manual
    # override slipped through. Fail loudly: the stock image can't run it.
    if [ "${IS_BONSAI:-0}" = "1" ]; then
        die "Bonsai on CPU is unsupported — no Prism CPU image exists (CUDA: $(prism_image_cuda), Vulkan: $(prism_image_vulkan)). Re-run with --model=qwen."
    fi
    _CPU_IMAGE="ghcr.io/ggml-org/llama.cpp:server"
    _CPU_COMMENT="# CPU-only configuration (auto-generated by install.sh)"
    cat > "$OVERRIDE_FILE" <<EOF
$_CPU_COMMENT
# Runtime knobs resolve from docker/.env (installer defaults after each :-)
# so tuning never needs a re-run: edit docker/.env, then
# `docker compose up -d llama-server`.
services:
  llama-server:
    image: \${LLAMA_IMAGE:-$_CPU_IMAGE}
    command: >
      --model /models/\${MODEL_NAME}
      --alias \${MODEL_ALIAS:-model}
      --host 0.0.0.0
      --port 7474
      --ctx-size \${CTX_SIZE:-$_CTX}
      --threads \${LLAMA_THREADS:-$CPU_THREADS}
      --threads-batch \${LLAMA_THREADS:-$CPU_THREADS}
      --batch-size \${LLAMA_BATCH:-2048}
      --ubatch-size \${LLAMA_UBATCH:-1024}
      --flash-attn \${LLAMA_FLASH_ATTN:-on}
      --cache-type-k \${KV_CACHE_K:-q8_0}
      --cache-type-v \${KV_CACHE_V:-q8_0}
      --parallel \${LLAMA_PARALLEL:-1}
      --jinja
      --reasoning off
      --metrics \${MODEL_MMPROJ:+--mmproj /models/\${MODEL_MMPROJ} --image-min-tokens 1024 --image-max-tokens 1280}
      \${LLAMA_API_KEY:+--api-key \${LLAMA_API_KEY}}
EOF
    ok "CPU override written"
    printf "     ${DIM}model: $MODEL_NAME  ·  ctx: $(( _CTX / 1024 ))k  ·  kv: q8_0  ·  threads: $CPU_THREADS (physical cores)  ·  vision: ${MODEL_MMPROJ:-disabled}${NC}\n"
    printf "     ${DIM}config: $OVERRIDE_FILE  (restart: docker compose up -d llama-server)${NC}\n"

    printf "\n  ${BOLD}${CYAN}Tuning knobs (docker/.env, then: docker compose up -d llama-server)${NC}\n"
    printf "     ${DIM}CTX_SIZE=N        halve to free ~half the KV-cache RAM, speeds up prompt processing${NC}\n"
    printf "     ${DIM}                  presets: 32k=32768  64k=65536  96k=98304  128k=131072  192k=196608${NC}\n"
    printf "     ${DIM}KV_CACHE_K/V      f16 (best) → q8_0 → q5_1 → q4_1 → q4_0 (least RAM)${NC}\n"
    printf "     ${DIM}LLAMA_THREADS=N   physical cores optimal ($CPU_THREADS); SMT/HT hurts llama.cpp throughput${NC}\n"
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

# DOCKER_ENV_FILE is defined before Step 6's branch. This block only runs for
# the non-Strata (llama) backend, so it records the model selection AND pins the
# backend marker to llama. Clearing any stale INFERENCE_BACKEND=strata here is
# what lets a box that previously ran Strata switch back to --model=qwen without
# the launcher (which reads this file) still trying to route to a Strata server.
if [ -f "$DOCKER_ENV_FILE" ]; then
    grep -v -E "^MODEL_NAME=|^MODEL_ALIAS=|^MODEL_MMPROJ=|^OPENMONO_VISION_ENABLED=|^CTX_SIZE=|^INFERENCE_BACKEND=" "$DOCKER_ENV_FILE" > "${DOCKER_ENV_FILE}.tmp" || true
    mv "${DOCKER_ENV_FILE}.tmp" "$DOCKER_ENV_FILE"
fi
printf "INFERENCE_BACKEND=llama\nMODEL_NAME=%s\nMODEL_ALIAS=%s\nMODEL_MMPROJ=%s\nCTX_SIZE=%s\n" "$MODEL_NAME" "$MODEL_ALIAS" "${MODEL_MMPROJ:-}" "${_CTX:-$CTX_CPU}" >> "$DOCKER_ENV_FILE"
if [ -n "${MODEL_MMPROJ:-}" ]; then
    printf "OPENMONO_VISION_ENABLED=1\n" >> "$DOCKER_ENV_FILE"
    om_persist_vision true
    detail "Persisted MODEL_NAME=$MODEL_NAME MODEL_MMPROJ=$MODEL_MMPROJ (vision enabled) to $DOCKER_ENV_FILE"
else
    printf "OPENMONO_VISION_ENABLED=0\n" >> "$DOCKER_ENV_FILE"
    om_persist_vision false
    detail "Persisted MODEL_NAME=$MODEL_NAME (vision disabled — no mmproj) to $DOCKER_ENV_FILE"
fi

fi  # End of Step 6 (skipped on agent role)

# ── Write llm.endpoint to ~/.openmono/settings.json (full + inference) ──
# The agent container and host-side readers resolve llm.endpoint from this
# file; the launcher rewrites loopback per-network (to the compose service
# name on Linux), so storing 127.0.0.1 works everywhere. Only fill it in when
# missing/empty — never clobber a relay endpoint the user configured.
if [ "$OPENMONO_ROLE" != "agent" ]; then
    SETTINGS_FILE="$HOME/.openmono/settings.json"
    mkdir -p "$(dirname "$SETTINGS_FILE")"
    [ -f "$SETTINGS_FILE" ] || echo '{}' > "$SETTINGS_FILE"
    if [ "${INFERENCE_BACKEND:-llama}" = "strata" ]; then
        _ep="http://127.0.0.1:${STRATA_PORT:-8080}/v1"
    else
        _ep="http://127.0.0.1:${LLAMA_PORT:-7474}"
    fi
    if [ -z "$(jq -r '.llm.endpoint // empty' "$SETTINGS_FILE" 2>/dev/null)" ]; then
        jq --arg e "$_ep" '.llm = ((.llm // {}) + {endpoint: $e})' "$SETTINGS_FILE" > "$SETTINGS_FILE.tmp" \
            && mv "$SETTINGS_FILE.tmp" "$SETTINGS_FILE"
        chmod 0600 "$SETTINGS_FILE" 2>/dev/null || true
        ok "llm.endpoint written to $SETTINGS_FILE ($_ep)"
    else
        detail "llm.endpoint already set in $SETTINGS_FILE — left untouched"
    fi
fi

# ── Step 7: Build Docker images (role-specific) ───────────────────────────────

next_step "Building Docker images"

cd "$INSTALL_DIR/docker"

info "Stopping any running containers..."
run docker compose down || true

# Only build the images this role actually needs. Skip the llama-server build for
# the agent role, the host-native Strata backend, and Bonsai (prebuilt Prism image).
if [ "$OPENMONO_ROLE" != "agent" ] && [ "$INFERENCE_BACKEND" != "strata" ]; then
    if [ "${IS_BONSAI:-0}" = "1" ]; then
        _PRISM_BACKEND="$(prism_backend)"
        info "Bonsai selected — using prebuilt Prism image from Docker Hub (no llama-server build)"
        detail "Image: $(prism_image_ref "$_PRISM_BACKEND")"
        prism_image_available "$_PRISM_BACKEND" || die "Prism image '$(prism_image_ref "$_PRISM_BACKEND")' missing — pull failed earlier"
    else
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
fi

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
if [ "$_build_agent" = "1" ]; then
    info "Building agent image..."
    if ! run docker compose build agent; then
        die "agent build failed"
    fi
elif [ "$OPENMONO_ROLE" = "inference" ]; then
    detail "Skipping agent image (inference-only box). Add later: docker compose build agent"
fi

if [ "$OPENMONO_ROLE" != "inference" ]; then
    # Optional add-on (agent roles only — never inference): the bare-metal host
    # sub-agent. It drives this box's in-container agent over ACP and executes
    # host commands (pull, build, install, redeploy, troubleshoot). Default is
    # No so a normal coding install is unchanged. Deliberately outside the
    # step counter so TOTAL_STEPS stays valid for every role.
    case "$OPENMONO_ROLE" in
        full|agent)
            _bridge_answer="${OPENMONO_HOST_BRIDGE:-}"
            if [[ -z "$_bridge_answer" ]]; then
                if [[ ! -t 0 ]]; then
                    info "Skipping host sub-agent (non-interactive, default No). Add it later with:"
                    info "  OPENMONO_HOST_BRIDGE=1 bash scripts/install.sh  # or: bash scripts/install-host-bridge.sh"
                else
                    echo ""
                    echo "  ── OPTIONAL: bare-metal host operator (DEFAULT: No) ──"
                    echo "  This BREAKS the container sandbox. If installed, the agent can"
                    echo "  run HOST commands directly on THIS HOST as your user (pull, build,"
                    echo "  install, docker, systemctl, redeploy, troubleshoot). Every command"
                    echo "  is audit-logged, sudo stays OFF unless you separately opt in."
                    echo "  Only install on a server box you operate. Coding laptops: say No."
                    echo "  You can add it later with: bash scripts/install-host-bridge.sh"
                    echo ""
                    printf "  Install the host sub-agent? Default No [y/N] (Enter=No): "
                    read -r _bridge_answer || _bridge_answer=""
                    echo ""
                fi
            fi
            case "${_bridge_answer:-}" in
                1|[Yy]|[Yy]es)
                    # Second explicit consent: "always" must be chosen, never assumed.
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
                        echo "  (service restarts, package installs, log reads). Choose how"
                        echo "  to handle privilege escalation:"
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
                *)
                    export OPENMONO_HOST_BRIDGE=0
                    ;;
            esac
            ;;
    esac
fi

ok "Docker images built"

# ── Step 8: Start inference backend (inference + full only) ──────────────────

if [ "$OPENMONO_ROLE" != "agent" ] && [ "$INFERENCE_BACKEND" = "strata" ]; then
    next_step "Starting Strata inference server"

    export STRATA_PORT="${STRATA_PORT:-8080}"
    if [ -f "$DOCKER_ENV_FILE" ]; then
        grep -v -E "^STRATA_PORT=" "$DOCKER_ENV_FILE" > "${DOCKER_ENV_FILE}.tmp" || true
        mv "${DOCKER_ENV_FILE}.tmp" "$DOCKER_ENV_FILE"
    fi
    echo "STRATA_PORT=${STRATA_PORT}" >> "$DOCKER_ENV_FILE"
    detail "Persisted STRATA_PORT=${STRATA_PORT} to $DOCKER_ENV_FILE"

    if ! bash "$SCRIPT_DIR/strata.sh" start; then
        warn "Strata did not become healthy yet — first boot can take several minutes."
        warn "Check: bash scripts/strata.sh logs   (or: openmono logs)"
    fi
fi

if [ "$OPENMONO_ROLE" != "agent" ] && [ "$INFERENCE_BACKEND" != "strata" ]; then
    next_step "Starting llama-server"

    LLAMA_PORT="${LLAMA_PORT:-7474}"

    port_in_use() {
        ss -tlnp 2>/dev/null | grep -q ":${1} " \
        || lsof -i ":${1}" &>/dev/null 2>&1
    }

    if port_in_use "$LLAMA_PORT"; then
        # If the port is held by our own llama-server container (this or a
        # previous install), stop it and restart on the SAME port — switching
        # ports to dodge the very service we are about to start is pointless.
        _port_holder=""
        if command -v docker &>/dev/null; then
            _port_holder="$(docker ps --filter 'name=llama-server' --format '{{.Names}}' 2>/dev/null | head -1 || true)"
        fi
        if [ -n "$_port_holder" ]; then
            info "Port ${LLAMA_PORT} is held by an existing llama-server container — restarting on the same port"
            docker stop "$_port_holder" >/dev/null 2>&1 || true
            for _i in $(seq 1 10); do
                port_in_use "$LLAMA_PORT" || break
                sleep 1
            done
        fi
        if port_in_use "$LLAMA_PORT"; then
            warn "Port ${LLAMA_PORT} is in use by another process"
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

# Vision on an agent box is NOT asked at install time — whether the remote
# endpoint actually serves images is detected at startup from the server's
# /props capabilities (modalities.vision), so there is no guess to go stale.
# Optional manual override: openmono config set inference.vision_enabled true|false

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
export OPENMONO_INFERENCE_BACKEND="${INFERENCE_BACKEND:-llama}"
export STRATA_PORT="${STRATA_PORT:-8080}"
export OPENMONO_HOST_BRIDGE="${OPENMONO_HOST_BRIDGE:-0}"
export MODEL_NAME="${MODEL_NAME:-}"
export MODEL_ACCURACY="${MODEL_ACCURACY:-standard}"
ENVEOF
    _log "Wrote install environment to: $OPENMONO_ENV_FILE"
else
    warn "OPENMONO_ENV_FILE not set (openmono cmd_setup should have set this)"
fi