#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Prerequisite Installer for Ubuntu
#
# Installs: Docker, git, cmake, curl, jq, .NET 10, ripgrep, build-essential,
#           (and CUDA + nvidia-container-toolkit if an NVIDIA GPU is detected).
#
# Options:
#   OPENMONO_VERBOSE=1    Show detailed command output
#
# Tested on: Ubuntu 24.04 LTS
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

# Ensure REPO_DIR is set (exported from openmono script)
if [[ -z "${REPO_DIR:-}" ]]; then
    REPO_DIR="$(dirname "$SCRIPT_DIR")"
fi

# Add openmono to PATH for current session
export PATH="$REPO_DIR:$PATH"
# (RC file updates are handled by openmono cmd_setup after installation completes)

# Configurable NVIDIA driver version (default: 580-server-open)
DRIVER_VERSION="${DRIVER_VERSION:-580-server-open}"

# GPU mode: determined by flags, persisted prefs, or user prompt (exported for install.sh)
GPU_MODE="${OPENMONO_GPU:-}"
if [[ -n "${OPENMONO_CPU:-}" ]]; then
    GPU_MODE=0
fi
# If not set by a flag, restore from a previous interrupted run
if [[ -z "$GPU_MODE" && -f "$HOME/.openmono/.setup_prefs" ]]; then
    _saved_gpu=$(grep '^GPU_MODE=' "$HOME/.openmono/.setup_prefs" 2>/dev/null | cut -d= -f2 | tr -d '[:space:]' || true)
    if [[ -n "$_saved_gpu" ]]; then
        GPU_MODE="$_saved_gpu"
        # Defer the "restoring" message until after log.sh helpers are confirmed sourced
        _GPU_MODE_RESTORED=true
    fi
fi
export GPU_MODE

# AMD iGPU mode: restored from prefs or set by user prompt
AMD_IGPU_MODE="${AMD_IGPU_MODE:-0}"
if [[ "$AMD_IGPU_MODE" = "0" && -f "$HOME/.openmono/.setup_prefs" ]]; then
    _saved_amd=$(grep '^AMD_IGPU_MODE=' "$HOME/.openmono/.setup_prefs" 2>/dev/null | cut -d= -f2 | tr -d '[:space:]' || true)
    if [[ "$_saved_amd" = "1" ]]; then
        AMD_IGPU_MODE=1
        _AMD_IGPU_MODE_RESTORED=true
    fi
fi
export AMD_IGPU_MODE

TOTAL_STEPS=8

banner "OpenMono.ai Prerequisites"

# ── Step 1: Detect OS ─────────────────────────────────────────────────────────

step 1 $TOTAL_STEPS "Detecting operating system"

if [ ! -f /etc/os-release ]; then
    die "Cannot detect OS. This script is designed for Ubuntu."
fi

. /etc/os-release

if [ "$ID" != "ubuntu" ]; then
    warn "Detected $PRETTY_NAME — this script targets Ubuntu."
    warn "Continuing, but some steps may need manual adjustment."
else
    ok "$PRETTY_NAME"
fi

# ── Step 2: Ensure sudo ───────────────────────────────────────────────────────

step 2 $TOTAL_STEPS "Checking privileges"

if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
    ok "Running as root"
else
    if ! command -v sudo &>/dev/null; then
        die "sudo is required. Run as root or install sudo first."
    fi
    SUDO="sudo"
    ok "sudo available"
fi

# ── Step 3: Update package index ──────────────────────────────────────────────

step 3 $TOTAL_STEPS "Updating package index"

if command -v dnf &>/dev/null; then
    info "Running dnf check-update..."
    # dnf check-update returns 100 if updates are available, 0 if not, 1 on error.
    run $SUDO dnf check-update || [ $? -eq 100 ] || die "Failed to check for updates"
    ok "Package index updated"
elif command -v apt-get &>/dev/null; then
    info "Running apt-get update..."
    if ! run $SUDO apt-get update -qq; then
        die "Failed to update apt package index"
    fi
    ok "Package index updated"
else
    warn "No known package manager found (apt/dnf) — skipping index update"
fi

# ── Step 4: Core tools (git, curl, cmake, build-essential, python3-pip) ──────

step 4 $TOTAL_STEPS "Installing core build tools"

install_pkg() {
    local pkg="$1"
    local check_cmd="${2:-$pkg}"
    if command -v "$check_cmd" &>/dev/null; then
        ok "$pkg already installed"
        detail "$(command -v "$check_cmd")"
    else
        info "Installing $pkg..."
        if command -v dnf &>/dev/null; then
            if ! run $SUDO dnf install -y -q "$pkg"; then
                die "Failed to install $pkg"
            fi
        else
            if ! run $SUDO apt-get install -y -qq "$pkg"; then
                die "Failed to install $pkg"
            fi
        fi
        ok "$pkg installed"
    fi
}

install_pkg git git
install_pkg curl curl
install_pkg jq jq
install_pkg cmake cmake
install_pkg pciutils lspci

_is_installed() {
    local pkg="$1"
    if command -v dnf &>/dev/null; then
        rpm -q "$pkg" &>/dev/null
    else
        dpkg -s "$pkg" &>/dev/null 2>&1
    fi
}

if _is_installed build-essential || { command -v gcc &>/dev/null && command -v make &>/dev/null; }; then
    ok "Build tools already installed"
else
    info "Installing build tools..."
    if command -v dnf &>/dev/null; then
        # dnf5 (Fedora 41+) removed `groupinstall`; `install @group` works on dnf4 and dnf5.
        run $SUDO dnf install -y -q @development-tools || die "Failed to install development tools"
    else
        run $SUDO apt-get install -y -qq build-essential || die "Failed to install build-essential"
    fi
    ok "Build tools installed"
fi

if command -v pip3 &>/dev/null; then
    ok "python3-pip already installed"
else
    info "Installing python3-pip..."
    if command -v dnf &>/dev/null; then
        run $SUDO dnf install -y -q python3-pip || die "Failed to install python3-pip"
    else
        run $SUDO apt-get install -y -qq python3-pip || die "Failed to install python3-pip"
    fi
    ok "python3-pip installed"
fi

if _is_installed libopenblas-dev || _is_installed openblas-devel; then
    ok "OpenBLAS development files already installed"
else
    info "Installing OpenBLAS development files..."
    if command -v dnf &>/dev/null; then
        run $SUDO dnf install -y -q openblas-devel || die "Failed to install openblas-devel"
    else
        run $SUDO apt-get install -y -qq libopenblas-dev pkg-config || die "Failed to install libopenblas-dev"
    fi
    ok "OpenBLAS development files installed"
fi

if command -v rg &>/dev/null; then
    ok "ripgrep already installed"
else
    info "Installing ripgrep..."
    if command -v dnf &>/dev/null; then
        run $SUDO dnf install -y -q ripgrep || die "Failed to install ripgrep"
    else
        run $SUDO apt-get install -y -qq ripgrep || die "Failed to install ripgrep"
    fi
    ok "ripgrep installed"
fi

# ── Step 5: NVIDIA stack (optional) ──────────────────────────────────────────

step 5 $TOTAL_STEPS "Checking for NVIDIA GPU"

if [[ "${OPENMONO_ROLE:-}" == "agent" ]]; then
    info "Agent-only role — skipping GPU detection"
    GPU_MODE=0
    HAS_NVIDIA_HW=false
else

HAS_NVIDIA_HW=false
# Try lspci first (most descriptive)
if command -v lspci &>/dev/null && lspci 2>/dev/null | grep -qi 'nvidia'; then
    HAS_NVIDIA_HW=true
    detail "$(lspci | grep -i nvidia | head -3)"
# Fallback to /sys (most reliable, no extra tools needed)
elif grep -qi "0x10de" /sys/bus/pci/devices/*/vendor 2>/dev/null; then
    HAS_NVIDIA_HW=true
    detail "NVIDIA GPU detected via PCI vendor ID (0x10de)"
fi

# Detect AMD Ryzen 9 7940HS + Radeon 780M iGPU
HAS_AMD_780M=false
_CPU_MODEL=$(grep -m1 "^model name" /proc/cpuinfo 2>/dev/null | sed 's/model name[[:space:]]*:[[:space:]]*//')
if echo "$_CPU_MODEL" | grep -qi "7940HS"; then
    if lspci 2>/dev/null | grep -qi "radeon\|amdgpu" \
        || lsmod 2>/dev/null | grep -q "^amdgpu "; then
        HAS_AMD_780M=true
        detail "AMD Ryzen 9 7940HS + Radeon 780M detected"
    fi
fi

# Determine GPU mode: explicit flag / persisted pref takes precedence, then auto-detect with prompt
if [[ -n "$GPU_MODE" ]]; then
    if [[ "${_GPU_MODE_RESTORED:-false}" == "true" ]]; then
        info "Restoring saved GPU mode from previous session: ${BOLD}$([ "$GPU_MODE" = "1" ] && echo GPU || echo CPU)${NC}"
    fi
elif [ "$HAS_NVIDIA_HW" = true ] || command -v nvidia-smi &>/dev/null; then
    if command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null 2>&1; then
        # Driver is already loaded and active (e.g. post-reboot re-run) — skip prompt
        GPU_MODE=1
        ok "NVIDIA GPU detected and driver active — GPU mode enabled"
    else
        # Hardware present but driver not yet active — ask the user
        echo ""
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        printf "${BLUE}${BOLD}  NVIDIA GPU Detected${NC}\n"
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        echo ""
        flush_stdin  # drop keystrokes buffered during earlier long steps
        _gpu_invalid=0
        while true; do
            [ "$_gpu_invalid" -eq 1 ] && printf "  ${RED}Please press Y or N.${NC}\n\n"
            printf "  Would you like to install on GPU? ${BOLD}(Y/n)${NC}: "
            read -r -n 1 _gpu_choice
            echo ""
            case "${_gpu_choice:-Y}" in
                [Yy]) GPU_MODE=1; break ;;
                [Nn]) GPU_MODE=0; break ;;
                *)    _gpu_invalid=1 ;;
            esac
        done
        _save_setup_pref "GPU_MODE" "$GPU_MODE"
        if [ "$GPU_MODE" = "1" ]; then
            echo ""
            info "NVIDIA drivers will be installed. They will only become active after a reboot."
            info "Setup will continue normally — you will be prompted to reboot at the end."
        fi
        echo ""
    fi
elif [[ "$HAS_AMD_780M" = true && "${OPENMONO_ROLE:-}" != "agent" ]]; then
    # AMD Ryzen 9 7940HS + Radeon 780M iGPU path
    GPU_MODE=0  # Keep GPU_MODE=0 so NVIDIA stack is skipped; use AMD_IGPU_MODE for iGPU selection
    if [[ "${_AMD_IGPU_MODE_RESTORED:-false}" == "true" ]]; then
        info "Restoring saved iGPU mode from previous session"
    elif [[ "$AMD_IGPU_MODE" = "1" ]]; then
        # Already set via env var
        info "iGPU mode requested"
    else
        # Prompt user for CPU vs iGPU
        echo ""
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        printf "${BLUE}${BOLD}  AMD Ryzen 9 7940HS + Radeon 780M Detected${NC}\n"
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        echo ""
        echo "  This iGPU can run models via Vulkan using system RAM as VRAM."
        echo ""
        echo "  1) CPU only   — standard inference (no kernel changes)"
        echo "  2) iGPU (Vulkan) — Radeon 780M acceleration (modifies kernel config)"
        echo ""
        flush_stdin  # drop keystrokes buffered during earlier long steps
        printf "  Choose mode [1=cpu, 2=igpu] [default: 1]: "
        read -r -n 1 _igpu_choice
        echo ""
        _igpu_choice="${_igpu_choice:-1}"
        if [[ "$_igpu_choice" = "2" ]]; then
            echo ""
            printf "  ${YELLOW}${BOLD}WARNING: Experimental${NC}\n"
            printf "  This will modify your kernel configuration.\n"
            printf "  Recommended for dedicated inference setups only!\n"
            echo ""
            flush_stdin  # else a buffered Enter skips this deliberate pause
            printf "  Press ENTER to continue or Ctrl+C to abort: "
            read -r _amd_confirm
            AMD_IGPU_MODE=1
        fi
        echo ""
    fi

    if [[ "$AMD_IGPU_MODE" = "1" ]]; then
        # Check if GRUB parameters are already active (system already rebooted after previous opt run)
        if grep -q "amdgpu.gttsize=28672" /proc/cmdline 2>/dev/null; then
            info "AMD iGPU kernel parameters already active — skipping optimization script"
        else
            bash "$SCRIPT_DIR/opt_7940hs.sh" --igpu || {
                err "AMD iGPU optimization failed"
                exit 1
            }
            # Signal that reboot is needed (set in parent shell, not via export from subshell)
            AMD_IGPU_REBOOT_PENDING=true
        fi
        # Only save pref AFTER completion (whether via opt script or already active)
        _save_setup_pref "AMD_IGPU_MODE" "1"
    fi
else
    # No NVIDIA hardware and no AMD iGPU detected
    GPU_MODE=0
fi

if [ "$GPU_MODE" = 0 ]; then
    info "GPU not detected — skipping CUDA/nvidia-container-toolkit"
    info "Switching to CPU mode automatically"
else
    ok "GPU mode enabled — installing NVIDIA stack"

    # Driver
    if command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null; then
        ok "NVIDIA drivers already installed"
        detail "$(nvidia-smi --query-gpu=name,driver_version --format=csv,noheader | head -1)"
    else
        info "Installing NVIDIA drivers..."
        warn "This may require a reboot before GPU is usable."
        if command -v dnf &>/dev/null; then
            run $SUDO dnf install -y -q akmod-nvidia xorg-x11-drv-nvidia-cuda || die "Failed to install NVIDIA drivers"
        else
            run $SUDO apt-get install -y -qq ubuntu-drivers-common || warn "ubuntu-drivers-common install had warnings"

            # Install nvidia-driver with configured version
            if ! run $SUDO apt-get install -y -qq "nvidia-driver-${DRIVER_VERSION}"; then
                warn "nvidia-driver-${DRIVER_VERSION} install had warnings"
            fi

            # Try nvidia-utils with configured version, fallback to 580-server if unavailable
            if apt-cache show "nvidia-utils-${DRIVER_VERSION}" &>/dev/null 2>&1; then
                run $SUDO apt-get install -y -qq "nvidia-utils-${DRIVER_VERSION}" || warn "nvidia-utils-${DRIVER_VERSION} install had warnings"
            else
                warn "nvidia-utils-${DRIVER_VERSION} not available, falling back to nvidia-utils-580-server"
                run $SUDO apt-get install -y -qq nvidia-utils-580-server || warn "nvidia-utils-580-server install had warnings"
            fi

            run $SUDO ubuntu-drivers autoinstall || warn "Driver install had warnings — check log"
        fi
        ok "NVIDIA drivers installed"
        NVIDIA_REBOOT_PENDING=true
    fi

    # CUDA toolkit (optional — pre-built images include CUDA)
    if command -v nvcc &>/dev/null; then
        ok "CUDA toolkit already installed"
        detail "$(nvcc --version | grep release)"
    else
        info "Installing CUDA toolkit..."
        if command -v dnf &>/dev/null; then
            run $SUDO dnf install -y -q xorg-x11-drv-nvidia-cuda || warn "CUDA toolkit install had warnings"
        else
            run $SUDO apt-get install -y -qq nvidia-cuda-toolkit || warn "CUDA toolkit install had warnings (not critical — Docker image includes CUDA)"
        fi
    fi

    # nvidia-container-toolkit (required for Docker GPU passthrough)
    if _is_installed nvidia-container-toolkit; then
        ok "nvidia-container-toolkit already installed"
    else
        info "Installing nvidia-container-toolkit..."
        if command -v dnf &>/dev/null; then
            run bash -c 'curl -s -L https://nvidia.github.io/libnvidia-container/stable/rpm/nvidia-container-toolkit.repo | '"$SUDO"' tee /etc/yum.repos.d/nvidia-container-toolkit.repo'
            run $SUDO dnf install -y -q nvidia-container-toolkit || die "Failed to install nvidia-container-toolkit"
        else
            run bash -c 'curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey | '"$SUDO"' gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg'
            run bash -c 'curl -s -L https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list | sed "s#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g" | '"$SUDO"' tee /etc/apt/sources.list.d/nvidia-container-toolkit.list >/dev/null'
            run $SUDO apt-get update -qq
            run $SUDO apt-get install -y -qq nvidia-container-toolkit || die "Failed to install nvidia-container-toolkit"
        fi
        ok "nvidia-container-toolkit installed"
    fi
fi

fi # end agent-role GPU skip

# Write GPU_MODE back to parent so install.sh doesn't re-detect independently
mkdir -p "$HOME/.openmono"
echo "GPU_MODE=$GPU_MODE" > "$HOME/.openmono/.tmp_gpu_mode"

# ── Step 6: Docker ────────────────────────────────────────────────────────────

step 6 $TOTAL_STEPS "Installing Docker"

INSTALL_DOCKER_CE=false

if command -v docker &>/dev/null; then
    # The `docker` command exists — but is it actually functional? On Windows
    # hosts with Docker Desktop and WSL integration *disabled* for this distro,
    # `docker` is a shim that errors with "could not be found in this WSL 2
    # distro" on every invocation. Offer to auto-replace it with native
    # docker-ce instead of leaving the user to debug.
    _docker_probe="$(docker version 2>&1 || true)"
    if echo "$_docker_probe" | grep -qiE "could not be found in this WSL|activate the WSL integration"; then
        warn "Docker Desktop WSL integration is disabled for this distro."
        warn "The 'docker' on your PATH is a Docker Desktop shim that won't work"
        warn "here until you either (a) enable WSL integration in Docker Desktop,"
        warn "or (b) install native docker-ce in this distro."
        echo ""

        # Default to auto-fix, but let OPENMONO_AUTO_REPLACE_DOCKER=0 or a
        # terminal prompt opt out. Env-var YES is useful for unattended / CI runs.
        _reply=""
        if [ "${OPENMONO_AUTO_REPLACE_DOCKER:-}" = "1" ]; then
            _reply=y
            info "OPENMONO_AUTO_REPLACE_DOCKER=1 — installing docker-ce without prompting."
        elif [ "${OPENMONO_AUTO_REPLACE_DOCKER:-}" = "0" ]; then
            _reply=n
        else
            flush_stdin  # drop keystrokes buffered during earlier long steps
            printf "  Install native docker-ce now? (removes Docker Desktop\n"
            printf "  integration in THIS WSL distro only — your host install\n"
            printf "  is untouched.)                                        [Y/n]: "
            read -r _reply || _reply=Y
            _reply="${_reply:-Y}"
        fi

        if [[ "$_reply" =~ ^[Yy] ]]; then
            info "Removing Docker Desktop WSL shim..."
            if command -v dnf &>/dev/null; then
                run $SUDO dnf remove -y docker-desktop 2>/dev/null || true
            else
                run $SUDO apt-get remove -y docker-desktop 2>/dev/null || true
            fi
            # Whether or not docker-desktop was an apt package, the shim may
            # exist as a dangling symlink under /usr/bin or /usr/local/bin.
            # Remove defensively so the docker-ce install can claim the path.
            run $SUDO rm -f \
                /usr/bin/docker /usr/bin/docker-compose /usr/bin/docker-credential-desktop \
                /usr/local/bin/docker /usr/local/bin/docker-compose 2>/dev/null || true
            hash -r
            INSTALL_DOCKER_CE=true
            ok "Docker Desktop shim removed; continuing with docker-ce install"
        else
            err "Opted out of auto-install. To fix manually:"
            err "  a) Docker Desktop → Settings → Resources → WSL Integration"
            err "     → toggle this distro ON → Apply & Restart, then:"
            err "       exec bash && openmono setup"
            if command -v dnf &>/dev/null; then
                err "  b) Or: sudo dnf remove -y docker-desktop && openmono setup"
            else
                err "  b) Or: sudo apt-get remove -y docker-desktop && openmono setup"
            fi
            die "Docker not functional."
        fi
    elif ! echo "$_docker_probe" | grep -q "Client:"; then
        # Command exists, doesn't produce a sensible 'docker version' — probably
        # daemon isn't running. Give a pointer rather than muddling through.
        err "Docker command exists but 'docker version' failed:"
        echo "$_docker_probe" | sed 's/^/  /' >&2
        die "Fix Docker (start the daemon or reinstall) and re-run openmono setup."
    else
        ok "Docker already installed"
        detail "$(docker --version)"
    fi
fi

if [ "$INSTALL_DOCKER_CE" = true ] || ! command -v docker &>/dev/null; then
    info "Installing Docker..."
    if command -v dnf &>/dev/null; then
        # Drop the Docker CE repo file directly. dnf5 (Fedora 41+) replaced
        # `config-manager --add-repo <url>` with the `addrepo` subcommand, so we
        # fetch the .repo file ourselves — works on dnf4 and dnf5 alike, and
        # mirrors the nvidia-container-toolkit repo handling above.
        run bash -c 'curl -fsSL https://download.docker.com/linux/fedora/docker-ce.repo | '"$SUDO"' tee /etc/yum.repos.d/docker-ce.repo >/dev/null'
        run $SUDO dnf install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin \
            || die "Docker install failed"
    else
        info "Adding Docker's official apt repository..."
        run $SUDO apt-get install -y -qq ca-certificates gnupg
        run $SUDO install -m 0755 -d /etc/apt/keyrings
        if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
            run bash -c 'curl -fsSL https://download.docker.com/linux/ubuntu/gpg | '"$SUDO"' gpg --dearmor -o /etc/apt/keyrings/docker.gpg'
            run $SUDO chmod a+r /etc/apt/keyrings/docker.gpg
        fi

        ARCH="$(dpkg --print-architecture)"
        CODENAME="$(. /etc/os-release && echo "$VERSION_CODENAME")"
        run bash -c "echo 'deb [arch=$ARCH signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $CODENAME stable' | $SUDO tee /etc/apt/sources.list.d/docker.list >/dev/null"

        info "Installing Docker packages..."
        run $SUDO apt-get update -qq
        run $SUDO apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin \
            || die "Docker install failed"
    fi
    run $SUDO groupadd docker 2>/dev/null || true
    run $SUDO usermod -aG docker "$USER" || true
    ok "Docker installed"

    # WSL2 doesn't start systemd services by default on some distros — make
    # sure the docker daemon is actually running before we try to use it.
    # service(8) works whether or not systemd is PID 1.
    if command -v service &>/dev/null; then
        run $SUDO service docker start 2>/dev/null || true
    fi
    if command -v systemctl &>/dev/null && systemctl list-unit-files docker.service &>/dev/null 2>&1; then
        run $SUDO systemctl enable --now docker 2>/dev/null || true
        # Boot persistence: the containers' `restart: always` policy only kicks in
        # if dockerd itself starts on boot. Verify the unit is actually enabled and
        # warn loudly if not — a disabled daemon is the usual reason the stack does
        # not come back after a reboot.
        if systemctl is-enabled --quiet docker 2>/dev/null; then
            ok "docker.service enabled — stack will auto-start on boot"
        else
            warn "docker.service is NOT enabled on boot; containers won't return after reboot."
            warn "Enable it with: sudo systemctl enable docker"
        fi
    fi

    # Note: docker group activation is handled by the openmono wrapper after
    # this script exits — it detects the new membership via getent and re-execs
    # the entire setup under sg docker. Do not re-exec here: re-running this
    # script would replay interactive prompts (e.g. GPU mode) a second time.
fi

# Ensure user is in the docker group even when Docker was pre-installed.
# The openmono wrapper handles sg re-exec after this script exits.
if command -v docker &>/dev/null && ! id -nG 2>/dev/null | grep -qw docker; then
    run $SUDO groupadd docker 2>/dev/null || true
    run $SUDO usermod -aG docker "$USER" || true
    ok "Added '$USER' to the docker group"
fi

if docker compose version &>/dev/null 2>&1; then
    ok "Docker Compose available"
    detail "$(docker compose version --short 2>/dev/null || echo 'plugin')"
else
    info "Docker Compose plugin missing — installing..."

    if command -v dnf &>/dev/null; then
         run $SUDO dnf install -y -q docker-compose-plugin || die "Failed to install docker-compose-plugin"
    else
        # If Docker was already installed (so we took the "already installed"
        # branch above), the Docker CE apt repo may not be configured. Add it
        # now so apt can find docker-compose-plugin.
        if [ ! -f /etc/apt/sources.list.d/docker.list ]; then
            info "Configuring Docker's apt repository first..."
            run $SUDO apt-get install -y -qq ca-certificates gnupg
            run $SUDO install -m 0755 -d /etc/apt/keyrings
            if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
                run bash -c 'curl -fsSL https://download.docker.com/linux/ubuntu/gpg | '"$SUDO"' gpg --dearmor -o /etc/apt/keyrings/docker.gpg'
                run $SUDO chmod a+r /etc/apt/keyrings/docker.gpg
            fi
            ARCH="$(dpkg --print-architecture)"
            CODENAME="$(. /etc/os-release && echo "$VERSION_CODENAME")"
            run bash -c "echo 'deb [arch=$ARCH signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $CODENAME stable' | $SUDO tee /etc/apt/sources.list.d/docker.list >/dev/null"
            run $SUDO apt-get update -qq
        fi

        if run $SUDO apt-get install -y -qq docker-compose-plugin; then
            ok "Docker Compose plugin installed"
            detail "$(docker compose version --short 2>/dev/null || echo 'plugin')"
        else
            warn "apt couldn't install docker-compose-plugin."
            warn "If you're on Docker Desktop (WSL / Mac), enable Compose via the"
            warn "Docker Desktop settings, or update Docker Desktop to a version"
            warn "that bundles the Compose v2 plugin, then re-run this installer."
        fi
    fi
fi

# Configure Docker nvidia runtime if GPU detected
if [ "$HAS_NVIDIA_HW" = true ] && command -v nvidia-ctk &>/dev/null; then
    if docker info 2>/dev/null | grep -q nvidia; then
        ok "Docker already configured with nvidia runtime"
    else
        info "Configuring Docker with nvidia runtime..."
        run $SUDO nvidia-ctk runtime configure --runtime=docker
        run $SUDO systemctl restart docker
        ok "Docker nvidia runtime configured"
    fi
fi

# ── Step 7: .NET 10 SDK ──────────────────────────────────────────────────────

step 7 $TOTAL_STEPS "Installing .NET 10 SDK"

if command -v dotnet &>/dev/null && dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
    ok ".NET 10 SDK already installed"
    detail "$(dotnet --version)"
else
    info "Downloading Microsoft dotnet-install script..."
    run curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    info "Installing .NET 10 to \$HOME/.dotnet (this can take a minute)..."
    run /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" \
        || die ".NET install failed"
    rm -f /tmp/dotnet-install.sh

    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"

    for rc in "$HOME/.bashrc" "$HOME/.zshrc" "$HOME/.profile"; do
        if [ -f "$rc" ] && ! grep -q "DOTNET_ROOT" "$rc"; then
            {
                echo ""
                echo "# .NET SDK"
                echo 'export DOTNET_ROOT="$HOME/.dotnet"'
                echo 'export PATH="$DOTNET_ROOT:$PATH"'
            } >> "$rc"
            detail "Added .NET to PATH in $(basename "$rc")"
        fi
    done
    ok ".NET 10 SDK installed ($(dotnet --version 2>/dev/null || echo 'reload shell'))"
fi

# ── Step 8: Summary ──────────────────────────────────────────────────────────

step 8 $TOTAL_STEPS "Verifying install"

check_installed() {
    local cmd="$1"
    if command -v "$cmd" &>/dev/null; then
        printf "  ${GREEN}✓${NC} %s\n" "$cmd"
    else
        printf "  ${YELLOW}…${NC} %s (may need shell reload)\n" "$cmd"
    fi
}

check_installed docker
check_installed git
check_installed jq
check_installed cmake
check_installed curl
check_installed rg
check_installed dotnet
if [ "$HAS_NVIDIA_HW" = true ]; then
    check_installed nvidia-ctk
    check_installed nvidia-smi
fi

echo ""
ok "Prerequisites ready"
echo ""
show_log_location

# ── Deferred reboot prompt (NVIDIA drivers installed this run) ────────────────

if [ "${NVIDIA_REBOOT_PENDING:-false}" = "true" ]; then
    _reboot_done_file="$HOME/.openmono/.nvidia_reboot_done"
    if [ -f "$_reboot_done_file" ] || (command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null 2>&1); then
        ok "NVIDIA drivers are active — no reboot needed"
    else
        echo ""
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        printf "${BLUE}${BOLD}  Reboot Required${NC}\n"
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        echo ""
        info "NVIDIA drivers are installed but will only become active after a reboot."
        echo ""
        # Critical: this prompt defaults to Y and reboots. A keystroke buffered
        # during the long driver/CUDA install must not auto-trigger it.
        flush_stdin
        _reboot_invalid=0
        while true; do
            [ "$_reboot_invalid" -eq 1 ] && printf "  ${RED}Please press Y or N.${NC}\n\n"
            printf "  Would you like to reboot now? ${BOLD}(Y/n)${NC}: "
            read -r -n 1 _reboot_choice
            echo ""
            case "${_reboot_choice:-Y}" in
                [Yy]) _reboot_choice=Y; break ;;
                [Nn]) _reboot_choice=N; break ;;
                *)    _reboot_invalid=1 ;;
            esac
        done

        if [[ "$_reboot_choice" == "Y" ]]; then
            touch "$_reboot_done_file"
            echo ""
            info "After reboot, run: ${BOLD}openmono setup${NC}"
            echo ""
            info "Rebooting in 10 seconds (press Ctrl+C to cancel)..."
            sleep 10
            $SUDO reboot
            exit 0
        else
            warn "Reboot skipped. NVIDIA drivers will not be active until you reboot."
            warn "Run: sudo reboot"
        fi
    fi
fi

# ── Deferred reboot prompt (AMD iGPU kernel optimizations applied this run) ─────

if [ "${AMD_IGPU_REBOOT_PENDING:-false}" = "true" ]; then
    echo ""
    printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    printf "${BLUE}${BOLD}  Reboot Required${NC}\n"
    printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    echo ""
    info "AMD iGPU kernel optimizations are installed but will only become active after a reboot."
    echo ""
    # Critical: this prompt defaults to Y and reboots. A keystroke buffered
    # during the long optimization step must not auto-trigger it.
    flush_stdin
    _reboot_invalid=0
    while true; do
        [ "$_reboot_invalid" -eq 1 ] && printf "  ${RED}Please press Y or N.${NC}\n\n"
        printf "  Would you like to reboot now? ${BOLD}(Y/n)${NC}: "
        read -r -n 1 _reboot_choice
        echo ""
        case "${_reboot_choice:-Y}" in
            [Yy]) _reboot_choice=Y; break ;;
            [Nn]) _reboot_choice=N; break ;;
            *)    _reboot_invalid=1 ;;
        esac
    done

    if [[ "$_reboot_choice" == "Y" ]]; then
        echo ""
        info "After reboot, run: ${BOLD}openmono setup${NC}"
        echo ""
        info "Rebooting in 10 seconds (press Ctrl+C to cancel)..."
        sleep 10
        $SUDO reboot
        exit 0
    else
        warn "Reboot skipped. AMD iGPU kernel parameters will not be active until you reboot."
        warn "Run: sudo reboot"
    fi
fi

# Write GPU_MODE and AMD_IGPU_MODE to the shared env file so install.sh picks them up without re-detecting
if [[ -n "${OPENMONO_ENV_FILE:-}" ]]; then
    echo "export GPU_MODE=\"${GPU_MODE:-0}\"" >> "$OPENMONO_ENV_FILE"
    echo "export AMD_IGPU_MODE=\"${AMD_IGPU_MODE:-0}\"" >> "$OPENMONO_ENV_FILE"
    echo "export AMD_IGPU_REBOOT_PENDING=\"${AMD_IGPU_REBOOT_PENDING:-false}\"" >> "$OPENMONO_ENV_FILE"
    _log "GPU_MODE=${GPU_MODE:-0}, AMD_IGPU_MODE=${AMD_IGPU_MODE:-0}, and AMD_IGPU_REBOOT_PENDING=${AMD_IGPU_REBOOT_PENDING:-false} written to env file"
fi
