#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Prerequisite Installer for macOS
#
# Installs: Homebrew, Xcode CLT, git, cmake, curl, jq, .NET 10, ripgrep, Docker
#           Desktop (and notes on Apple Silicon support).
#
# Options:
#   OPENMONO_VERBOSE=1    Show detailed command output
#
# Tested on: macOS 14+ (Sonoma, Sequoia) with Apple Silicon (M1+) and Intel
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

TOTAL_STEPS=8

banner "OpenMono.ai Prerequisites (macOS)"

# ── Step 1: Detect OS and architecture ────────────────────────────────────────

step 1 $TOTAL_STEPS "Detecting OS and architecture"

OS_NAME=$(uname -s)
ARCH=$(uname -m)

if [ "$OS_NAME" != "Darwin" ]; then
    die "This script is designed for macOS (Darwin). Detected: $OS_NAME"
fi

case "$ARCH" in
    arm64)
        ok "Apple Silicon (ARM64) detected"
        ARCH_NAME="Apple Silicon"
        BREW_PREFIX="/opt/homebrew"
        ;;
    x86_64)
        ok "Intel Mac (x86_64) detected"
        ARCH_NAME="Intel"
        BREW_PREFIX="/usr/local"
        ;;
    *)
        die "Unsupported architecture: $ARCH"
        ;;
esac

MACOS_VERSION=$(sw_vers -productVersion)
detail "macOS $MACOS_VERSION ($ARCH_NAME)"

# ── Step 2: Ensure sudo (if not root) ─────────────────────────────────────────

step 2 $TOTAL_STEPS "Checking privileges"

if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
    ok "Running as root"
else
    if ! command -v sudo &>/dev/null; then
        die "sudo is required"
    fi
    SUDO="sudo"
    ok "sudo available"
fi

# ── Step 3: Install/update Homebrew ───────────────────────────────────────────

step 3 $TOTAL_STEPS "Setting up Homebrew"

if command -v brew &>/dev/null; then
    ok "Homebrew already installed"
    detail "$(brew --version)"
else
    info "Installing Homebrew..."
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

    # Add Homebrew to PATH for this session (Apple Silicon uses /opt/homebrew)
    if [ -d "$BREW_PREFIX" ]; then
        eval "$("$BREW_PREFIX"/bin/brew shellenv)"
        ok "Homebrew installed"
        detail "$(brew --version)"
    else
        die "Homebrew installation failed"
    fi
fi

# Update Homebrew
info "Updating Homebrew..."
if ! run brew update -q; then
    die "Failed to update Homebrew"
fi
ok "Homebrew updated"

# Add Homebrew to PATH for this session
export PATH="$BREW_PREFIX/bin:$PATH"

# ── Step 4: Core tools (git, curl, cmake, ripgrep, openblas, pkg-config) ──────

step 4 $TOTAL_STEPS "Installing core build tools"

install_pkg() {
    local pkg="$1"
    local check_cmd="${2:-$pkg}"
    if command -v "$check_cmd" &>/dev/null || brew list "$pkg" &>/dev/null 2>&1; then
        ok "$pkg already installed"
    else
        info "Installing $pkg..."
        if ! run brew install "$pkg"; then
            die "Failed to install $pkg"
        fi
        ok "$pkg installed"
    fi
}

install_pkg git git
install_pkg curl curl
install_pkg jq jq
install_pkg cmake cmake
install_pkg ripgrep rg
install_pkg openblas
install_pkg pkg-config pkg-config

# Xcode Command Line Tools (replaces Linux's build-essential)
if xcode-select -p &>/dev/null; then
    ok "Xcode Command Line Tools already installed"
else
    info "Installing Xcode Command Line Tools (this may take a few minutes)..."
    xcode-select --install
    # Wait for installation to complete
    until xcode-select -p &>/dev/null; do
        sleep 5
    done
    ok "Xcode Command Line Tools installed"
fi

# Python 3 and pip (Homebrew's python3 includes pip3)
if command -v python3 &>/dev/null; then
    ok "python3 already installed"
    detail "$(python3 --version)"
else
    info "Installing python3..."
    if ! run brew install python3; then
        die "Failed to install python3"
    fi
    ok "python3 installed"
fi

if command -v pip3 &>/dev/null; then
    ok "pip3 already installed"
else
    die "pip3 not found after python3 install"
fi

# ── Step 5: Apple Silicon / Metal notes ────────────────────────────────────────

step 5 $TOTAL_STEPS "Apple Silicon configuration"

if [ "$ARCH" = "arm64" ]; then
    ok "Apple Silicon (M-series) detected"
    info "Docker on macOS runs a Linux VM — no direct Metal GPU passthrough to containers."
    info "The inference server will run in CPU mode for now."
    detail "Note: Future enhancement could use Docker Model Runner with vllm-metal backend."
else
    ok "Intel Mac — Docker CPU mode (standard)"
fi

# ── Step 6: Docker — Desktop or Colima ──────────────────────────────────────

step 6 $TOTAL_STEPS "Setting up Docker"

# Priority 1: Docker Desktop is installed — prefer it, skip Colima
DOCKER_DAEMON_TYPE=""
if [ -d "/Applications/Docker.app" ]; then
    DOCKER_DAEMON_TYPE="docker-desktop"
    ok "Docker Desktop is installed"
    if ! docker info &>/dev/null 2>&1; then
        info "Launching Docker Desktop..."
        open /Applications/Docker.app
        info "Waiting for Docker daemon to be accessible..."
        for i in $(seq 1 60); do
            if docker info &>/dev/null 2>&1; then
                ok "Docker daemon is now accessible"
                break
            fi
            sleep 1
            printf "."
        done
        echo ""
        if ! docker info &>/dev/null 2>&1; then
            warn "Docker daemon is still not accessible after 60s."
            warn "Docker.app may still be starting — try again in a moment."
        fi
    else
        ok "Docker daemon is running"
    fi
else
    DOCKER_DAEMON_TYPE="colima"
    # Priority 2: Docker Desktop not installed — use Colima
    # First, install Docker CLI (required for all subsequent docker commands)
    if command -v docker &>/dev/null; then
        ok "Docker CLI already installed"
        detail "$(docker --version)"
    else
        info "Installing Docker CLI via Homebrew..."
        if ! run brew install docker; then
            die "Failed to install Docker CLI"
        fi
        ok "Docker CLI installed"
    fi

    # Now install and start Colima
    if command -v colima &>/dev/null; then
        ok "Colima already installed"
    else
        info "Installing Colima (lightweight Docker daemon)..."
        if ! run brew install colima; then
            die "Failed to install Colima"
        fi
        ok "Colima installed"
    fi

    if ! docker info &>/dev/null 2>&1; then
        info "Starting Colima daemon..."
        if ! run colima start; then
            die "Failed to start Colima. Check: colima status"
        fi

        info "Waiting for Docker daemon to be ready..."
        for i in $(seq 1 30); do
            if docker info &>/dev/null 2>&1; then
                ok "Docker daemon is running"
                break
            fi
            sleep 1
            printf "."
        done
        echo ""

        if ! docker info &>/dev/null 2>&1; then
            die "Docker daemon failed to start after 30s. Check: colima status"
        fi
    fi

    # Install docker-buildx (required for Colima path to build images)
    if docker buildx version &>/dev/null 2>&1; then
        ok "Docker buildx already available"
    else
        info "Installing docker-buildx..."
        if run brew install docker-buildx; then
            ok "Docker buildx installed"
        else
            warn "Docker buildx installation failed. Image builds may fail."
        fi
    fi
fi

# Install Docker Compose (prefer v2 plugin, but v1 works too)
if docker compose version &>/dev/null 2>&1; then
    ok "Docker Compose v2 already available"
    detail "$(docker compose version 2>/dev/null)"
elif command -v docker-compose &>/dev/null; then
    ok "Docker Compose v1 already available"
    detail "$(docker-compose --version 2>/dev/null)"
else
    info "Installing Docker Compose via Homebrew..."
    if run brew install docker-compose; then
        ok "Docker Compose installed"
        detail "$(docker-compose --version 2>/dev/null || echo 'installed')"
    else
        warn "Docker Compose installation failed — install manually: brew install docker-compose"
    fi
fi

# Wire docker-compose and docker-buildx as CLI plugins so `docker compose` and `docker buildx` work
_COMPOSE_BIN="$(brew --prefix 2>/dev/null)/opt/docker-compose/bin/docker-compose"
if [ -f "$_COMPOSE_BIN" ]; then
    mkdir -p "$HOME/.docker/cli-plugins"
    ln -sfn "$_COMPOSE_BIN" "$HOME/.docker/cli-plugins/docker-compose"
    detail "Linked docker-compose as Docker CLI plugin"
fi

_BUILDX_BIN="$(brew --prefix 2>/dev/null)/opt/docker-buildx/bin/docker-buildx"
if [ -f "$_BUILDX_BIN" ]; then
    mkdir -p "$HOME/.docker/cli-plugins"
    ln -sfn "$_BUILDX_BIN" "$HOME/.docker/cli-plugins/docker-buildx"
    detail "Linked docker-buildx as Docker CLI plugin"
fi

# Register Colima for auto-start at login (if Colima was installed)
if command -v colima &>/dev/null && command -v brew &>/dev/null; then
    info "Registering Colima to start automatically at login..."
    if brew services start colima 2>/dev/null; then
        ok "Colima registered for auto-start at login (via launchd)"
    else
        warn "Could not register Colima for auto-start."
        warn "After each reboot, run: colima start"
        warn "Or register manually: brew services start colima"
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

    # Add to shell rc files (prioritize zsh on macOS, also add bash)
    for rc in "$HOME/.zshrc" "$HOME/.bash_profile" "$HOME/.bashrc"; do
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
check_installed docker-compose
if [ "$DOCKER_DAEMON_TYPE" = "colima" ]; then
    check_installed docker-buildx
fi
check_installed git
check_installed jq
check_installed cmake
check_installed curl
check_installed rg
check_installed python3
check_installed dotnet

echo ""
ok "Prerequisites ready"

# Homebrew shell integration note
if [ "$ARCH" = "arm64" ]; then
    echo ""
    printf "${YELLOW}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    printf "${YELLOW}${BOLD}  Homebrew PATH Integration${NC}\n"
    printf "${YELLOW}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    echo ""
    echo "  For Apple Silicon Macs, add Homebrew to your shell:"
    echo ""
    echo "    ${BOLD}eval \"\$(/opt/homebrew/bin/brew shellenv)\"${NC}"
    echo ""
    echo "  This is typically auto-added by Homebrew to:"
    echo "    ~/.zprofile  (Zsh login shell)"
    echo "    ~/.zshrc  (Zsh interactive shell)"
    echo "    ~/.bash_profile  (Bash login shell)"
    echo ""
    echo "  Reload your shell if Homebrew commands are not found:"
    echo "    ${BOLD}exec zsh${NC}  or  ${BOLD}exec bash${NC}"
    echo ""
fi

show_log_location
