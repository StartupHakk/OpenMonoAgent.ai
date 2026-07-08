#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Prerequisite Installer for macOS
#
# Installs prerequisites based on OPENMONO_ROLE (set by the openmono CLI):
#   full      — Homebrew, Xcode CLT, core tools, llama.cpp (arm64), Docker, .NET
#   inference — Homebrew, Xcode CLT, core tools, llama.cpp (arm64) only — no Docker
#   agent     — Homebrew, Xcode CLT, core tools, Docker, .NET — no llama.cpp
#
# Options:
#   OPENMONO_ROLE=full|inference|agent  (exported by openmono CLI; defaults to full)
#   OPENMONO_VERBOSE=1                  Show detailed command output
#
# Tested on: macOS 14+ (Sonoma, Sequoia) with Apple Silicon (M1+)
# Intel Macs: supported for agent role only (no llama.cpp / inference)
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

# Role determines which components are installed. Default to full if not set.
OPENMONO_ROLE="${OPENMONO_ROLE:-full}"

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

# Add Homebrew to shell rc files for future sessions
for rc_file in "$HOME/.zprofile" "$HOME/.bash_profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
    if [ -f "$rc_file" ] && ! grep -q "brew shellenv" "$rc_file"; then
        {
            echo ""
            echo "# Homebrew"
            echo "eval \"\$($BREW_PREFIX/bin/brew shellenv)\""
        } >> "$rc_file"
        detail "Added Homebrew to PATH in $(basename "$rc_file")"
    fi
done

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
# Minimum version: 3.10 (required by code-review-graph and graphifyy)
MIN_PYTHON_VERSION="3.10"

check_python_version() {
    local version_output major_minor

    if ! command -v python3 &>/dev/null; then
        return 1
    fi

    version_output=$(python3 --version 2>&1)
    major_minor=$(echo "$version_output" | grep -oE '[0-9]+\.[0-9]+')

    # Compare versions: if MIN is <= current, then current is OK (return 0)
    if [ "$(printf '%s\n' "$MIN_PYTHON_VERSION" "$major_minor" | sort -V | head -n 1)" = "$MIN_PYTHON_VERSION" ]; then
        return 0  # version is sufficient
    else
        return 1  # version is too old
    fi
}

if check_python_version; then
    ok "python3 already installed"
    detail "$(python3 --version) ✓ (minimum required: $MIN_PYTHON_VERSION)"
else
    if command -v python3 &>/dev/null; then
        warn "python3 version is older than $MIN_PYTHON_VERSION (current: $(python3 --version))"
    fi
    info "Installing/upgrading python3 to latest stable..."
    if ! run brew install python3; then
        die "Failed to install/upgrade python3"
    fi
    ok "python3 ready ($(python3 --version))"
fi

if command -v pip3 &>/dev/null; then
    ok "pip3 already installed"
else
    die "pip3 not found after python3 install"
fi

# ── Step 5: llama.cpp (Apple Silicon only) ────────────────────────────────────

step 5 $TOTAL_STEPS "Setting up native inference (Apple Silicon)"

if [ "$ARCH" = "arm64" ]; then
    # llama.cpp from Homebrew runs natively on the host with Metal GPU acceleration.
    # This is required for the full and inference roles on macOS.
    if command -v llama-server &>/dev/null; then
        ok "llama.cpp already installed ($(llama-server --version 2>/dev/null | head -1))"
    else
        info "Installing llama.cpp (Metal backend)..."
        if ! run brew install llama.cpp; then
            die "Failed to install llama.cpp"
        fi
        ok "llama.cpp installed"
    fi
    detail "llama-server: $(command -v llama-server)"
else
    # Intel Macs have no Metal GPU and no unified memory.
    # Native inference via llama.cpp is not supported — skipping install.
    warn "Intel Mac detected — llama.cpp (Metal) will NOT be installed."
    warn "The 'full' and 'inference' roles are only supported on Apple Silicon."
    warn "You can still use the 'agent' role and connect to a remote inference server."
fi

# ── Step 6: Docker — Desktop or Colima (skipped for inference role) ──────────

step 6 $TOTAL_STEPS "Setting up Docker"

if [ "$OPENMONO_ROLE" = "inference" ]; then
    ok "Docker not required for inference role — skipping"
    DOCKER_DAEMON_TYPE="none"
else

BREW_PREFIX=$(brew --prefix)
DOCKER_PLUGINS="$HOME/.docker/cli-plugins"
mkdir -p "$DOCKER_PLUGINS"

# Priority 1: Use Docker Desktop if it exists
if [ -d "/Applications/Docker.app" ]; then
    DOCKER_DAEMON_TYPE="docker-desktop"
    ok "Docker Desktop detected"
    
    if ! docker info &>/dev/null; then
        info "Launching Docker Desktop (background)..."
        open -g /Applications/Docker.app
    fi
else
    # Priority 2: Standardize on Colima for scripted installs
    DOCKER_DAEMON_TYPE="colima"
    info "Docker Desktop not found. Ensuring Colima environment is ready..."

    # Install stack
    for pkg in docker colima docker-compose docker-buildx; do
        if brew list "$pkg" &>/dev/null; then
            ok "$pkg is already installed"
        else
            info "Installing $pkg via Homebrew..."
            run brew install "$pkg"
        fi
    done

    # Setup CLI plugins (links 'docker compose' and 'docker buildx' commands)
    ln -sfn "$BREW_PREFIX/opt/docker-compose/bin/docker-compose" "$DOCKER_PLUGINS/docker-compose"
    ln -sfn "$BREW_PREFIX/opt/docker-buildx/bin/docker-buildx" "$DOCKER_PLUGINS/docker-buildx"
    ok "Docker CLI plugins linked"

    ok "Colima installed"
fi

ok "Docker environment configured"

# Ensure Docker daemon is running (matches Ubuntu's systemctl enable --now)
if ! docker info &>/dev/null 2>&1; then
    if [ "$DOCKER_DAEMON_TYPE" = "docker-desktop" ]; then
        info "Docker Desktop is not responding. Relaunching..."
        open -g /Applications/Docker.app
    elif [ "$DOCKER_DAEMON_TYPE" = "colima" ]; then
        info "Starting Colima daemon..."
        colima start --activate 2>/dev/null || true
    fi

    # Wait for Docker to be ready (works for both Docker Desktop and Colima)
    info "Waiting for Docker daemon to be ready..."
    for i in $(seq 1 60); do
        if docker info &>/dev/null 2>&1; then
            ok "Docker daemon is now running"
            break
        fi
        sleep 1
        printf "."
    done
    echo ""

    if ! docker info &>/dev/null 2>&1; then
        warn "Docker daemon did not become ready within 60s."
        warn "It may still be starting. Continue with setup, and restart if needed:"
        if [ "$DOCKER_DAEMON_TYPE" = "docker-desktop" ]; then
            warn "  open /Applications/Docker.app"
        else
            warn "  colima start"
        fi
    fi
else
    ok "Docker daemon is already running"
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

if [ "$OPENMONO_ROLE" != "inference" ]; then
    check_installed docker
    check_installed docker-compose
    if [ "$DOCKER_DAEMON_TYPE" = "colima" ]; then
        check_installed docker-buildx
    fi
fi
check_installed git
check_installed jq
check_installed cmake
check_installed curl
check_installed rg
check_installed python3
check_installed dotnet
if [ "$ARCH" = "arm64" ] && [ "$OPENMONO_ROLE" != "agent" ]; then
    check_installed llama-server
fi

echo ""
ok "Prerequisites ready"
echo ""
show_log_location
