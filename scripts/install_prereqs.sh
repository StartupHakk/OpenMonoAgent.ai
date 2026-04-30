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

# Add to shell rc files for future sessions (Ubuntu/Linux)
for rc_file in "$HOME/.bashrc" "$HOME/.zshrc"; do
    if [ -f "$rc_file" ] && ! grep -q "export PATH=.*$REPO_DIR" "$rc_file"; then
        {
            echo ""
            echo "# OpenMono.ai CLI"
            echo "export PATH=$REPO_DIR:\$PATH"
        } >> "$rc_file"
    fi
done

# Configurable NVIDIA driver version (default: 580-server-open)
DRIVER_VERSION="${DRIVER_VERSION:-580-server-open}"

# GPU mode: determined by flags or user prompt (exported for install.sh)
GPU_MODE="${OPENMONO_GPU:-}"
if [[ -n "${OPENMONO_CPU:-}" ]]; then
    GPU_MODE=0
fi
export GPU_MODE

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

step 3 $TOTAL_STEPS "Updating apt package index"

info "Running apt-get update..."
if ! run $SUDO apt-get update -qq; then
    die "Failed to update apt package index"
fi
ok "Package index updated"

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
        if ! run $SUDO apt-get install -y -qq "$pkg"; then
            die "Failed to install $pkg"
        fi
        ok "$pkg installed"
    fi
}

install_pkg git git
install_pkg curl curl
install_pkg jq jq
install_pkg cmake cmake
install_pkg pciutils lspci

if dpkg -s build-essential &>/dev/null 2>&1; then
    ok "build-essential already installed"
else
    info "Installing build-essential..."
    run $SUDO apt-get install -y -qq build-essential || die "Failed to install build-essential"
    ok "build-essential installed"
fi

if command -v pip3 &>/dev/null; then
    ok "python3-pip already installed"
else
    info "Installing python3-pip..."
    run $SUDO apt-get install -y -qq python3-pip || die "Failed to install python3-pip"
    ok "python3-pip installed"
fi

if dpkg -s libopenblas-dev &>/dev/null 2>&1; then
    ok "libopenblas-dev already installed"
else
    info "Installing libopenblas-dev..."
    run $SUDO apt-get install -y -qq libopenblas-dev pkg-config || die "Failed to install libopenblas-dev"
    ok "libopenblas-dev installed"
fi

if command -v rg &>/dev/null; then
    ok "ripgrep already installed"
else
    info "Installing ripgrep..."
    run $SUDO apt-get install -y -qq ripgrep || die "Failed to install ripgrep"
    ok "ripgrep installed"
fi

# ── Step 5: NVIDIA stack (optional) ──────────────────────────────────────────

step 5 $TOTAL_STEPS "Checking for NVIDIA GPU"

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

# Determine GPU mode: explicit flag takes precedence, then auto-detect with prompt
if [[ -z "$GPU_MODE" ]]; then
    # No flag provided — auto-detect and prompt if NVIDIA hardware is found
    if [ "$HAS_NVIDIA_HW" = true ] || command -v nvidia-smi &>/dev/null; then
        echo ""
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        printf "${BLUE}${BOLD}  NVIDIA GPU Detected${NC}\n"
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        echo ""
        printf "  Would you like to install on GPU? ${BOLD}(Y/n)${NC}: "
        read -r _gpu_choice
        _gpu_choice="${_gpu_choice:-Y}"
        if [[ "$_gpu_choice" =~ ^[Yy]$ ]]; then
            GPU_MODE=1
        else
            GPU_MODE=0
        fi
        echo ""
    else
        # No NVIDIA hardware detected
        GPU_MODE=0
    fi
fi

if [ "$GPU_MODE" = 0 ]; then
    info "GPU mode disabled — skipping CUDA/nvidia-container-toolkit"
else
    ok "GPU mode enabled — installing NVIDIA stack"

    # Driver
    if command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null; then
        ok "NVIDIA drivers already installed"
        detail "$(nvidia-smi --query-gpu=name,driver_version --format=csv,noheader | head -1)"
    else
        info "Installing NVIDIA drivers (version: $DRIVER_VERSION)..."
        warn "This may require a reboot before GPU is usable."
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
        ok "NVIDIA drivers installed"

        echo ""
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        printf "${BLUE}${BOLD}  NVIDIA drivers installed — reboot required${NC}\n"
        printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
        echo ""
        printf "  Would you like to reboot now? ${BOLD}(Y/n)${NC}: "
        read -r _reboot_choice
        _reboot_choice="${_reboot_choice:-Y}"

        if [[ "$_reboot_choice" =~ ^[Yy]$ ]]; then
            echo ""
            info "After reboot, run: ${BOLD}openmono setup${NC}"
            echo ""
            info "Rebooting in 10 seconds (press Ctrl+C to cancel)..."
            sleep 10
            $SUDO reboot
        else
            err "Reboot cancelled. NVIDIA drivers will not be functional until rebooted."
            err "Run: sudo reboot"
            exit 1
        fi
    fi

    # CUDA toolkit (optional — pre-built images include CUDA)
    if command -v nvcc &>/dev/null; then
        ok "CUDA toolkit already installed"
        detail "$(nvcc --version | grep release)"
    else
        info "Installing nvidia-cuda-toolkit..."
        run $SUDO apt-get install -y -qq nvidia-cuda-toolkit || warn "CUDA toolkit install had warnings (not critical — Docker image includes CUDA)"
    fi

    # nvidia-container-toolkit (required for Docker GPU passthrough)
    if dpkg -s nvidia-container-toolkit &>/dev/null 2>&1; then
        ok "nvidia-container-toolkit already installed"
    else
        info "Installing nvidia-container-toolkit..."
        run bash -c 'curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey | '"$SUDO"' gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg'
        run bash -c 'curl -s -L https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list | sed "s#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g" | '"$SUDO"' tee /etc/apt/sources.list.d/nvidia-container-toolkit.list >/dev/null'
        run $SUDO apt-get update -qq
        run $SUDO apt-get install -y -qq nvidia-container-toolkit || die "Failed to install nvidia-container-toolkit"
        ok "nvidia-container-toolkit installed"
    fi
fi

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
            printf "  Install native docker-ce now? (removes Docker Desktop\n"
            printf "  integration in THIS WSL distro only — your host install\n"
            printf "  is untouched.)                                        [Y/n]: "
            read -r _reply || _reply=Y
            _reply="${_reply:-Y}"
        fi

        if [[ "$_reply" =~ ^[Yy] ]]; then
            info "Removing Docker Desktop WSL shim..."
            run $SUDO apt-get remove -y docker-desktop 2>/dev/null || true
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
            err "  b) Or: sudo apt-get remove -y docker-desktop && openmono setup"
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
    fi

    # Re-exec this script under 'sg docker' so all remaining steps (and the
    # subsequent install.sh) run with the docker group active. The user
    # never needs to manually run 'newgrp docker'. exec replaces the current
    # process; the re-run is fast because every apt/command check hits the
    # already-installed branch.
    if ! docker info &>/dev/null 2>&1 && id -nG 2>/dev/null | grep -qw docker; then
        if command -v sg &>/dev/null && sg docker -c "docker info" &>/dev/null 2>&1; then
            info "Re-launching with docker group active (no manual 'newgrp' needed)..."
            exec sg docker -- bash "$0" "$@"
        else
            warn "Docker group added but sg activation failed — a shell restart is needed."
            warn "After this script completes run ONE of the following, then re-run install.sh:"
            warn "  1. newgrp docker           (activate in current shell)"
            warn "  2. exec su -l \$USER       (fresh login shell)"
            warn "  3. Log out and back in"
        fi
    fi
fi

if docker compose version &>/dev/null 2>&1; then
    ok "Docker Compose available"
    detail "$(docker compose version --short 2>/dev/null || echo 'plugin')"
else
    info "Docker Compose plugin missing — installing..."

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

# Check if docker is accessible without sudo
DOCKER_NEEDS_GROUP=false
if command -v docker &>/dev/null; then
    if ! docker info &>/dev/null 2>&1; then
        DOCKER_NEEDS_GROUP=true
    fi
fi

echo ""
ok "Prerequisites ready"

if [ "$DOCKER_NEEDS_GROUP" = true ]; then
    echo ""
    printf "${YELLOW}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    printf "${YELLOW}${BOLD}  Docker Group Activation Required${NC}\n"
    printf "${YELLOW}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    echo ""
    echo "  The 'sg' command could not activate the docker group automatically."
    echo "  Run ONE of the following before running install.sh:"
    echo ""
    echo "    ${BOLD}newgrp docker${NC}              # Activate in current shell"
    echo "    ${BOLD}exec su -l \$USER${NC}          # Start fresh login shell"
    echo "    ${BOLD}Log out and back in${NC}        # Full session refresh"
    echo ""
    echo "  After activation, verify with: docker info"
    echo "  Then run: ./scripts/install.sh"
    echo ""
fi

show_log_location
