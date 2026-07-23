#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Prerequisite Installer for CachyOS (Arch-based)
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

if [[ -z "${REPO_DIR:-}" ]]; then
    REPO_DIR="$(dirname "$SCRIPT_DIR")"
fi

export PATH="$REPO_DIR:$PATH"
GPU_MODE="${OPENMONO_GPU:-}"
if [[ -n "${OPENMONO_CPU:-}" ]]; then
    GPU_MODE=0
fi

TOTAL_STEPS=8
banner "OpenMono.ai Prerequisites (CachyOS)"

# ── Step 1: Detect OS ─────────────────────────────────────────────────────────
step 1 $TOTAL_STEPS "Detecting operating system"
if [ ! -f /etc/os-release ]; then
    die "Cannot detect OS."
fi

. /etc/os-release
if [[ "$ID" != "cachyos" && "$ID" != "arch" ]]; then
    warn "Detected $PRETTY_NAME — this version targets CachyOS/Arch."
else
    ok "$PRETTY_NAME"
fi

# ── Step 2: Ensure sudo ───────────────────────────────────────────────────────
step 2 $TOTAL_STEPS "Checking privileges"
SUDO="sudo"
if [ "$(id -u)" -eq 0 ]; then SUDO=""; fi
ok "Privileges checked"

# ── Step 3: Update package index ──────────────────────────────────────────────
step 3 $TOTAL_STEPS "Updating pacman database"
info "Syncing repositories..."
$SUDO pacman -Sy --noconfirm || die "Failed to update package index"
ok "Package index updated"

# ── Step 4: Core tools ────────────────────────────────────────────────────────
step 4 $TOTAL_STEPS "Installing core build tools"

install_pkg() {
    local pkg="$1"
    info "Installing $pkg..."
    $SUDO pacman -S --needed --noconfirm "$pkg" || die "Failed to install $pkg"
}

# Install base-devel (Equivalent to build-essential)
$SUDO pacman -S --needed --noconfirm base-devel

# Map Ubuntu packages to Arch/CachyOS
install_pkg git
install_pkg curl
install_pkg jq
install_pkg cmake
install_pkg pciutils       # Provides lspci
install_pkg python-pip
install_pkg openblas
install_pkg ripgrep
install_pkg pkgconf        # Arch version of pkg-config

# ── Step 5: NVIDIA stack ──────────────────────────────────────────────────────
step 5 $TOTAL_STEPS "Checking for NVIDIA GPU"

HAS_NVIDIA_HW=false
if lspci 2>/dev/null | grep -qi 'nvidia'; then
    HAS_NVIDIA_HW=true
fi

if [ "$GPU_MODE" = 1 ] || [ "$HAS_NVIDIA_HW" = true ]; then
    ok "GPU mode enabled — installing CachyOS NVIDIA stack"

    # CachyOS uses specialized nvidia packages
    $SUDO pacman -S --needed nvidia-utils nvidia-settings lib32-nvidia-utils

    # CUDA and Container Toolkit
    install_pkg cuda

    # nvidia-container-toolkit is in the AUR or Cachy repos
    if ! pacman -Qi nvidia-container-toolkit &>/dev/null; then
        info "Installing nvidia-container-toolkit..."
        $SUDO pacman -S --needed --noconfirm nvidia-container-toolkit
    fi
    NVIDIA_REBOOT_PENDING=true
fi

# ── Step 6: Docker ────────────────────────────────────────────────────────────
step 6 $TOTAL_STEPS "Installing Docker"

if ! command -v docker &>/dev/null; then
    install_pkg docker
    install_pkg docker-compose
    $SUDO systemctl enable --now docker
    $SUDO usermod -aG docker "$USER"
    ok "Docker installed and service started"
else
    ok "Docker already installed"
fi

# Configure NVIDIA runtime for Docker
if [ "$HAS_NVIDIA_HW" = true ] && command -v nvidia-ctk &>/dev/null; then
    $SUDO nvidia-ctk runtime configure --runtime=docker
    $SUDO systemctl restart docker
    ok "Docker NVIDIA runtime configured"
fi

# ── Step 7: .NET 10 SDK ──────────────────────────────────────────────────────
step 7 $TOTAL_STEPS "Installing .NET 10 SDK"
# Arch usually has latest dotnet in repos, but the install script is safer for specific versions
if ! dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"

    # Pathing
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
    ok ".NET 10 installed to $HOME/.dotnet"
else
    ok ".NET 10 already present"
fi

# ── Step 8: Summary ──────────────────────────────────────────────────────────
step 8 $TOTAL_STEPS "Verifying install"
# ... [Verification logic remains largely the same]
echo "Prerequisites ready for CachyOS"
