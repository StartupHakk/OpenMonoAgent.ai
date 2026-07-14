#!/usr/bin/env bash
set -uo pipefail
# NOTE: -e intentionally disabled — uninstall must keep going even when a step
# fails (e.g. a container is already gone, an apt package was never installed,
# /usr/local/bin isn't writable). Every command's failure mode is handled
# locally with `|| true` or an explicit check.

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai Uninstaller
#
# Reverses install.sh (always) and — with --deep — install_prereqs.sh.
#
# What this script removes by default (safe, OpenMono-only):
#   • llama-server + agent + gateway + searxng + scrapling Docker containers
#   • Locally-built / pulled images tied to the compose file
#   • $INSTALL_DIR/docker/docker-compose.override.yml   (GPU/CPU override)
#   • /usr/local/bin/openmono                            (CLI symlink)
#   • frp tunnel: frpc binary/pkg + config + service     (from 'openmono tunnel setup')
#   • $HOME/.openmono/                                   (graph-db, prefs, logs, env)
#   • Power profile is restored to 'balanced'            (Linux + powerprofilesctl only)
#   • RC-file additions for $HOME/.dotnet PATH/DOTNET_ROOT (.bashrc/.zshrc/.profile)
#
# Prompted for separately (large / hard to re-create):
#   • Downloaded model file(s) under $INSTALL_DIR/models/
#   • The cloned repository at $INSTALL_DIR
#
# Only with --deep (reverses install_prereqs.sh; Ubuntu only):
#   • docker-ce + docker-ce-cli + containerd.io + buildx + compose plugins
#   • Docker apt repo + keyring
#   • nvidia-container-toolkit + apt repo + keyring
#   • nvidia-cuda-toolkit
#   • nvidia-driver-* + nvidia-utils-* (warns: drivers are system-wide)
#   • .NET 10 SDK at $HOME/.dotnet
#   • Python packages installed --user: code-review-graph, graphifyy
#
# Never removed (even with --deep):
#   • git, curl, jq, cmake, ripgrep, build-essential, python3-pip — common
#     system tools that other software relies on. Remove them manually if
#     you really want to.
#   • Homebrew, Xcode CLT, Docker Desktop on macOS — too pervasive.
#
# Options:
#   --deep               Also remove apt/system packages installed by prereqs
#   --keep-model         Don't prompt to remove the downloaded model
#   --keep-repo          Don't prompt to remove the cloned repo
#   --keep-dotnet        Don't remove .NET SDK in --deep mode
#   --keep-docker        Don't remove docker-ce in --deep mode
#   --keep-nvidia        Don't remove NVIDIA drivers/CUDA in --deep mode
#   --yes, -y            Skip all confirmation prompts (DANGEROUS)
#   --dry-run            Print what would be removed; change nothing
#   --verbose            Show detailed command output (also OPENMONO_VERBOSE=1)
#   -h, --help           Show this help
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Redirect the log out of ~/.openmono before sourcing log.sh, because step 6
# of this script deletes ~/.openmono — we'd otherwise destroy our own log
# mid-run and leave the user nothing to share for diagnostics.
if [ -z "${OPENMONO_LOG_FILE:-}" ]; then
    export OPENMONO_LOG_FILE="$HOME/openmono-uninstall-$(date +%Y%m%d-%H%M%S).log"
    : > "$OPENMONO_LOG_FILE"
fi

# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

# ── Arg parsing ───────────────────────────────────────────────────────────────

DEEP=0
KEEP_MODEL=0
KEEP_REPO=0
KEEP_DOTNET=0
KEEP_DOCKER=0
KEEP_NVIDIA=0
ASSUME_YES=0
DRY_RUN=0

usage() {
    sed -n '/^# ──/,/^# ──/p' "$0" | head -60 | sed 's/^# \{0,1\}//'
    exit 0
}

for arg in "$@"; do
    case "$arg" in
        --deep)         DEEP=1 ;;
        --keep-model)   KEEP_MODEL=1 ;;
        --keep-repo)    KEEP_REPO=1 ;;
        --keep-dotnet)  KEEP_DOTNET=1 ;;
        --keep-docker)  KEEP_DOCKER=1 ;;
        --keep-nvidia)  KEEP_NVIDIA=1 ;;
        --yes|-y)       ASSUME_YES=1 ;;
        --dry-run)      DRY_RUN=1 ;;
        --verbose)      export OPENMONO_VERBOSE=1 ;;
        -h|--help)      usage ;;
        *)              err "Unknown option: $arg"; echo "" >&2; usage ;;
    esac
done

# ── Helpers ───────────────────────────────────────────────────────────────────

# do_run — wrapper around `run` that respects --dry-run and never aborts the
# script on failure. Logs the would-run command in dry-run mode.
do_run() {
    if [ "$DRY_RUN" = "1" ]; then
        printf "  ${DIM}[dry-run]${NC} %s\n" "$*"
        _log "DRY-RUN: $*"
        return 0
    fi
    run "$@" || _log "FAILED (continuing): $*"
}

# confirm — yes/no prompt with default. Respects --yes (auto-confirm) and
# --dry-run (auto-decline destructive prompts so the user sees the full plan).
# Returns 0 for yes, 1 for no.
#   confirm "Remove the model?" "Y"   # default yes (just Enter)
#   confirm "Remove the repo?"  "N"   # default no  (just Enter)
confirm() {
    local prompt="$1"
    local default="${2:-N}"
    local choice

    if [ "$ASSUME_YES" = "1" ]; then
        info "$prompt → assuming YES (--yes)"
        return 0
    fi
    if [ "$DRY_RUN" = "1" ]; then
        info "$prompt → would prompt (--dry-run, defaulting NO)"
        return 1
    fi

    local _invalid=0
    while true; do
        [ "$_invalid" -eq 1 ] && printf "  ${RED}Please press Y or N.${NC}\n\n"
        if [ "$default" = "Y" ] || [ "$default" = "y" ]; then
            printf "  %s ${BOLD}(Y/n)${NC}: " "$prompt"
        else
            printf "  %s ${BOLD}(y/N)${NC}: " "$prompt"
        fi
        read -r -n 1 choice
        echo ""
        choice="${choice:-$default}"
        case "$choice" in
            [Yy]) return 0 ;;
            [Nn]) return 1 ;;
            *)    _invalid=1 ;;
        esac
    done
}

# rm_path — remove a file or directory if it exists, log the action.
rm_path() {
    local target="$1"
    local label="${2:-$target}"
    if [ -e "$target" ] || [ -L "$target" ]; then
        if [ "$DRY_RUN" = "1" ]; then
            printf "  ${DIM}[dry-run]${NC} rm -rf %s\n" "$target"
            _log "DRY-RUN: rm -rf $target"
            return 0
        fi
        if rm -rf "$target" 2>>"$OPENMONO_LOG_FILE"; then
            ok "Removed $label"
            return 0
        fi
        if [ -n "${SUDO:-}" ]; then
            detail "$label not fully removable as $(id -un 2>/dev/null || echo "$USER") — retrying with $SUDO (container-owned files?)"
            if $SUDO rm -rf "$target" 2>>"$OPENMONO_LOG_FILE"; then
                ok "Removed $label (with $SUDO)"
                return 0
            fi
        fi
        warn "Could not remove $label (permission denied?)"
    else
        detail "$label not present — skipping"
    fi
}

# sudo_rm_path — like rm_path, but elevates with $SUDO. Used for the root-owned
# files frpc's `openmono tunnel setup` installs (/usr/local/bin/frpc, /etc/frp,
# the systemd unit). Respects --dry-run; soft-fails so a denied rm doesn't abort.
sudo_rm_path() {
    local target="$1"
    local label="${2:-$target}"
    if [ -e "$target" ] || [ -L "$target" ]; then
        if [ "$DRY_RUN" = "1" ]; then
            printf "  ${DIM}[dry-run]${NC} %s rm -rf %s\n" "$SUDO" "$target"
            _log "DRY-RUN: $SUDO rm -rf $target"
            return 0
        fi
        if $SUDO rm -rf "$target" 2>>"$OPENMONO_LOG_FILE"; then
            ok "Removed $label"
        else
            warn "Could not remove $label (permission denied?)"
        fi
    else
        detail "$label not present — skipping"
    fi
}

# detect_sudo — return "sudo" if needed, "" if root, error if no sudo.
detect_sudo() {
    if [ "$(id -u)" -eq 0 ]; then
        echo ""
    elif command -v sudo &>/dev/null; then
        echo "sudo"
    else
        echo "__no_sudo__"
    fi
}

# uninstall_apt_pkg — apt-get remove --purge a package if installed.
# Soft-fails so missing packages don't abort the run.
uninstall_apt_pkg() {
    local pkg="$1"
    if dpkg -s "$pkg" &>/dev/null 2>&1; then
        info "Removing $pkg..."
        do_run $SUDO apt-get remove --purge -y -qq "$pkg"
        ok "$pkg removed"
    else
        detail "$pkg not installed — skipping"
    fi
}

# strip_rc_block — remove the .NET SDK PATH block install_prereqs.sh added.
# Idempotent: a no-op if the block isn't there. Backs the file up to .bak first.
strip_rc_block() {
    local rc="$1"
    [ -f "$rc" ] || { detail "$rc not present — skipping"; return 0; }
    if ! grep -q "DOTNET_ROOT" "$rc"; then
        detail "$rc has no .NET block — skipping"
        return 0
    fi
    if [ "$DRY_RUN" = "1" ]; then
        printf "  ${DIM}[dry-run]${NC} strip .NET block from %s\n" "$rc"
        _log "DRY-RUN: strip .NET block from $rc"
        return 0
    fi
    # Match the exact block install_prereqs.sh writes (4 lines, with the
    # leading blank line). awk handles the multi-line delete atomically.
    cp "$rc" "${rc}.openmono-uninstall.bak"
    awk '
        # Detect the 4-line block: blank, "# .NET SDK", export DOTNET_ROOT,
        # export PATH (containing DOTNET_ROOT). Drop all four; keep the rest.
        BEGIN { skip = 0 }
        skip > 0 { skip--; next }
        /^[[:space:]]*$/ {
            # peek ahead via getline buffer
            blank = $0
            if ((getline next1) > 0) {
                if (next1 ~ /^# \.NET SDK[[:space:]]*$/) {
                    if ((getline next2) > 0 && next2 ~ /DOTNET_ROOT.*\.dotnet/) {
                        if ((getline next3) > 0 && next3 ~ /PATH.*DOTNET_ROOT/) {
                            # All 4 lines matched — drop them.
                            next
                        } else {
                            print blank; print next1; print next2; print next3; next
                        }
                    } else {
                        print blank; print next1; print next2; next
                    }
                } else {
                    print blank; print next1; next
                }
            } else {
                print blank; next
            }
        }
        { print }
    ' "${rc}.openmono-uninstall.bak" > "$rc" 2>>"$OPENMONO_LOG_FILE" || {
        warn "Failed to strip .NET block from $rc — restoring backup"
        mv "${rc}.openmono-uninstall.bak" "$rc"
        return 1
    }
    ok "Stripped .NET block from $(basename "$rc")  (backup: $(basename "$rc").openmono-uninstall.bak)"
}

# pip_uninstall — pip uninstall a --user package if installed.
pip_uninstall() {
    local pkg="$1"
    [ -z "$PIP_CMD" ] && { detail "no pip available — skipping $pkg"; return 0; }
    if $PIP_CMD show "$pkg" &>/dev/null 2>&1; then
        info "Uninstalling $pkg (pip)..."
        do_run $PIP_CMD uninstall -y "$pkg"
        ok "$pkg uninstalled"
    else
        detail "$pkg not installed via pip — skipping"
    fi
}

# ── Banner ────────────────────────────────────────────────────────────────────

banner "OpenMono.ai Uninstaller"

if [ "$DRY_RUN" = "1" ]; then
    warn "Dry-run mode: showing what WOULD be removed, but changing nothing."
    echo ""
fi

# ── Resolve install directory ────────────────────────────────────────────────
# Same precedence as install.sh, with one extra fallback: follow the
# /usr/local/bin/openmono symlink if it exists. If we still can't find it,
# bail rather than guessing — better than nuking the wrong directory.

# Compute TOTAL_STEPS up front so the "Step N/M" counter stays accurate.
# Safe mode = 10; --deep on Ubuntu adds pip + docker + nvidia + dotnet = 14;
# --deep on macOS only does pip + dotnet (the rest is brew/Docker-Desktop = NO).
# We resolve the platform briefly here too, just for the step count — the full
# platform detection happens a few lines down so the rest of the script can use it.
case "$(uname -s)" in
    Linux)  _is_ubuntu_quick=0; [ -f /etc/os-release ] && grep -qE '^(ID=ubuntu|ID=linuxmint|ID_LIKE=.*ubuntu)' /etc/os-release && _is_ubuntu_quick=1 ;;
    *)      _is_ubuntu_quick=0 ;;
esac

if [ "$DEEP" = "1" ] && [ "$_is_ubuntu_quick" = "1" ]; then
    TOTAL_STEPS=14
elif [ "$DEEP" = "1" ]; then
    TOTAL_STEPS=12
else
    TOTAL_STEPS=10
fi

CURRENT_STEP=0
next_step() { CURRENT_STEP=$((CURRENT_STEP + 1)); step $CURRENT_STEP $TOTAL_STEPS "$1"; }

next_step "Resolving install directory"

INSTALL_DIR=""
if [ -f "$SCRIPT_DIR/../OpenMono.sln" ]; then
    INSTALL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
    detail "Resolved via script location"
elif [ -n "${OPENMONO_HOME:-}" ] && [ -f "$OPENMONO_HOME/OpenMono.sln" ]; then
    INSTALL_DIR="$OPENMONO_HOME"
    detail "Resolved via OPENMONO_HOME"
elif [ -L "/usr/local/bin/openmono" ]; then
    _link_target="$(readlink -f /usr/local/bin/openmono 2>/dev/null || readlink /usr/local/bin/openmono)"
    if [ -n "$_link_target" ] && [ -f "$(dirname "$_link_target")/OpenMono.sln" ]; then
        INSTALL_DIR="$(cd "$(dirname "$_link_target")" && pwd)"
        detail "Resolved via /usr/local/bin/openmono symlink"
    fi
fi
if [ -z "$INSTALL_DIR" ] && [ -f "$HOME/openmono.ai/OpenMono.sln" ]; then
    INSTALL_DIR="$HOME/openmono.ai"
    detail "Resolved via default location"
fi

if [ -z "$INSTALL_DIR" ]; then
    warn "Could not locate the OpenMono install directory."
    warn "Looked at: \$SCRIPT_DIR/.., \$OPENMONO_HOME, /usr/local/bin/openmono, ~/openmono.ai"
    warn "Docker/repo/model cleanup will be skipped. Continuing with state-only cleanup."
else
    ok "Install directory: $INSTALL_DIR"
fi

# ── Detect platform ───────────────────────────────────────────────────────────

PLATFORM="unknown"
IS_UBUNTU=0
case "$(uname -s)" in
    Linux)
        PLATFORM="linux"
        if [ -f /etc/os-release ]; then
            # shellcheck disable=SC1091
            . /etc/os-release
            # Ubuntu and its apt-compatible derivatives (Linux Mint, Pop!_OS, …)
            # install the same packages the same way, so --deep can reverse them.
            if [ "${ID:-}" = "ubuntu" ] || [ "${ID:-}" = "linuxmint" ] || printf '%s' "${ID_LIKE:-}" | grep -qw ubuntu; then
                IS_UBUNTU=1
            fi
        fi
        ;;
    Darwin) PLATFORM="macos" ;;
esac
detail "Platform: $PLATFORM (ubuntu=$IS_UBUNTU)"

SUDO="$(detect_sudo)"
if [ "$SUDO" = "__no_sudo__" ]; then
    warn "Neither running as root nor sudo available — package removal will be skipped."
    SUDO=""
fi

PIP_CMD=""
if command -v pip3 &>/dev/null; then PIP_CMD="pip3"
elif command -v pip &>/dev/null; then PIP_CMD="pip"
fi

# ── Pre-flight summary + confirm ─────────────────────────────────────────────

echo ""
printf "  ${BOLD}Will remove:${NC}\n"
[ -n "$INSTALL_DIR" ] && printf "    • OpenMono Docker containers (llama, agent, gateway, searxng, scrapling), images, and override file\n"
printf "    • /usr/local/bin/openmono symlink (if writable)\n"
printf "    • frp tunnel — frpc service, binary/package, and config (from 'openmono tunnel setup')\n"
printf "    • \$HOME/.openmono/ (prefs, graph-db, logs, env)\n"
printf "    • Power profile reset to 'balanced' (if powerprofilesctl is present)\n"
printf "    • .NET PATH/DOTNET_ROOT block from your shell rc files\n"
if [ "$DEEP" = "1" ]; then
    printf "  ${BOLD}--deep also removes${NC} (Ubuntu only):\n"
    [ "$KEEP_DOCKER" = "0" ] && printf "    • docker-ce + apt repo + keyring\n"
    [ "$KEEP_NVIDIA" = "0" ] && printf "    • NVIDIA drivers, CUDA, container toolkit, repo + keyring\n"
    [ "$KEEP_DOTNET" = "0" ] && printf "    • .NET 10 SDK in \$HOME/.dotnet\n"
    printf "    • code-review-graph + graphifyy (pip --user)\n"
fi
if [ "$KEEP_MODEL" = "0" ] || [ "$KEEP_REPO" = "0" ]; then
    printf "  ${BOLD}You'll be asked separately about:${NC}\n"
    [ "$KEEP_MODEL" = "0" ] && printf "    • Downloaded model file(s) under \$INSTALL_DIR/models/\n"
    [ "$KEEP_REPO"  = "0" ] && printf "    • The cloned repository itself\n"
fi
echo ""

if ! confirm "Proceed with uninstall?" "Y"; then
    info "Aborted by user."
    exit 0
fi

# ── Step 2: Stop and remove Docker containers + images ──────────────────────

next_step "Stopping and removing Docker containers"

if [ -n "$INSTALL_DIR" ] && [ -d "$INSTALL_DIR/docker" ] && command -v docker &>/dev/null; then
    if docker info &>/dev/null 2>&1 || sg docker -c "docker info" &>/dev/null 2>&1; then
        cd "$INSTALL_DIR/docker"
        # --rmi local removes only images built by this compose project (the
        # agent image). The pulled llama.cpp images are removed by service-name
        # cleanup below — leaving --rmi local protects against blowing away an
        # unrelated 'llama.cpp:server' the user has tagged for another use.
        info "Running 'docker compose down --rmi local --volumes --remove-orphans'..."
        do_run docker compose down --rmi local --volumes --remove-orphans
        ok "Compose stack torn down"

        # Belt-and-suspenders: containers named explicitly in the compose file.
        for c in llama-server openmono-agent openmono-gateway openmono-searxng openmono-scrapling; do
            if docker ps -a --format '{{.Names}}' 2>/dev/null | grep -qx "$c"; then
                info "Force-removing leftover container: $c"
                do_run docker rm -f "$c"
            fi
        done

        # Remove the llama.cpp images that install.sh references. Don't touch
        # any other image. Use docker image inspect to check existence first.
        for img in \
            ghcr.io/ggml-org/llama.cpp:server \
            ghcr.io/ggml-org/llama.cpp:server-cuda \
            ghcr.io/ggml-org/llama.cpp:server-vulkan \
            caddy:2-alpine \
            searxng/searxng:latest
        do
            if docker image inspect "$img" &>/dev/null 2>&1; then
                if confirm "Remove pulled image $img?" "Y"; then
                    do_run docker image rm "$img"
                fi
            fi
        done
        cd "$SCRIPT_DIR"
    else
        warn "Docker is installed but not reachable (daemon down?) — skipping container removal."
    fi
else
    detail "Docker / compose dir not present — skipping container removal"
fi

# ── Step 3: Remove docker-compose override file ──────────────────────────────

next_step "Removing docker-compose override"

if [ -n "$INSTALL_DIR" ]; then
    rm_path "$INSTALL_DIR/docker/docker-compose.override.yml" "docker-compose.override.yml"
fi

# ── Step 4: Remove model file(s) ─────────────────────────────────────────────

next_step "Removing downloaded model(s)"

if [ -n "$INSTALL_DIR" ] && [ -d "$INSTALL_DIR/models" ]; then
    _model_size="$(du -sh "$INSTALL_DIR/models" 2>/dev/null | cut -f1)"
    info "Found models directory at $INSTALL_DIR/models  (${_model_size:-unknown size})"

    if [ "$KEEP_MODEL" = "1" ]; then
        info "Keeping model files (--keep-model)"
    elif confirm "Remove all model files (frees disk; re-download takes ~15-30 min)?" "Y"; then
        rm_path "$INSTALL_DIR/models" "models directory"
    else
        info "Keeping model files"
    fi
else
    detail "No models directory present — skipping"
fi

# ── Step 5: Remove /usr/local/bin/openmono symlink ───────────────────────────

next_step "Removing /usr/local/bin/openmono symlink"

if [ -L /usr/local/bin/openmono ] || [ -f /usr/local/bin/openmono ]; then
    if [ -w /usr/local/bin ]; then
        rm_path "/usr/local/bin/openmono" "/usr/local/bin/openmono"
    elif [ -n "$SUDO" ]; then
        if [ "$DRY_RUN" = "1" ]; then
            printf "  ${DIM}[dry-run]${NC} %s rm -f /usr/local/bin/openmono\n" "$SUDO"
            _log "DRY-RUN: $SUDO rm -f /usr/local/bin/openmono"
        else
            do_run $SUDO rm -f /usr/local/bin/openmono
            ok "Removed /usr/local/bin/openmono"
        fi
    else
        warn "/usr/local/bin not writable and no sudo — leave manually: rm /usr/local/bin/openmono"
    fi
else
    detail "/usr/local/bin/openmono not present — skipping"
fi

# ── Step 6: Stop and remove the frp tunnel (frpc) ────────────────────────────
# Reverses `openmono tunnel setup` (scripts/setup-tunnel-inference.sh):
#   Linux : systemd unit + /usr/local/bin/frpc + /etc/frp — all root-owned, so
#           every removal here elevates via $SUDO (or prints manual steps when
#           neither root nor sudo is available).
#   macOS : the Homebrew frpc service + package + ~/.config/frp + brew etc/frp.
# The relay credential cache (~/.openmono/relay.json) is removed with
# ~/.openmono in the next step.

next_step "Removing frp tunnel (frpc)"

if [ "$PLATFORM" = "macos" ]; then
    if command -v brew &>/dev/null; then
        if brew services list 2>/dev/null | grep -q '^frpc'; then
            info "Stopping frpc Homebrew service..."
            do_run brew services stop frpc
        fi
        if brew list frpc &>/dev/null 2>&1; then
            if confirm "Uninstall the frpc Homebrew package?" "Y"; then
                do_run brew uninstall frpc
                ok "Uninstalled frpc (Homebrew)"
            else
                info "Keeping frpc Homebrew package"
            fi
        else
            detail "frpc not installed via Homebrew — skipping"
        fi
        # brew service config copied here by setup-tunnel-inference.sh.
        _brew_frp="$(brew --prefix 2>/dev/null || true)/etc/frp"
        [ "$_brew_frp" != "/etc/frp" ] && rm_path "$_brew_frp" "Homebrew frp config"
    else
        detail "Homebrew not present — skipping frpc package removal"
    fi
    rm_path "$HOME/.config/frp" "\$HOME/.config/frp"
else
    # Linux: everything frpc installed is root-owned — needs root/sudo.
    if [ -z "$SUDO" ] && [ "$(id -u)" -ne 0 ]; then
        warn "frpc files are root-owned but neither root nor sudo is available."
        warn "Remove them manually:"
        warn "  sudo systemctl disable --now frpc"
        warn "  sudo rm -f /etc/systemd/system/frpc.service /usr/local/bin/frpc"
        warn "  sudo rm -rf /etc/frp && sudo systemctl daemon-reload"
    else
        # Stop + disable before deleting the unit (also clears the enable symlink
        # in multi-user.target.wants that `systemctl enable` created).
        if command -v systemctl &>/dev/null \
           && { [ -f /etc/systemd/system/frpc.service ] \
                || systemctl list-unit-files 2>/dev/null | grep -q '^frpc\.service'; }; then
            info "Stopping and disabling frpc systemd service..."
            do_run $SUDO systemctl disable --now frpc
        else
            detail "frpc systemd service not present — skipping stop/disable"
        fi
        sudo_rm_path "/etc/systemd/system/frpc.service" "frpc systemd unit"
        command -v systemctl &>/dev/null && do_run $SUDO systemctl daemon-reload
        sudo_rm_path "/usr/local/bin/frpc" "/usr/local/bin/frpc"
        sudo_rm_path "/etc/frp"            "/etc/frp (frpc config)"
    fi
fi

# ── Step 7: Remove $HOME/.openmono/ ──────────────────────────────────────────

next_step "Removing $HOME/.openmono/ state"

if [ -d "$HOME/.openmono" ]; then
    info "Contents of \$HOME/.openmono:"
    if [ -t 1 ]; then
        ls -1 "$HOME/.openmono" 2>/dev/null | sed 's/^/    • /' || true
    fi
    # Note: install.sh writes its setup logs under ~/.openmono/logs — those go
    # with the directory. Our own uninstall log was redirected to $HOME at
    # script start so it survives this step (see top of file).
    rm_path "$HOME/.openmono" "\$HOME/.openmono"
else
    detail "\$HOME/.openmono not present — skipping"
fi

# ── Step 8: Strip .NET block from shell rc files ─────────────────────────────

next_step "Stripping .NET SDK PATH from shell rc files"

for rc in "$HOME/.bashrc" "$HOME/.zshrc" "$HOME/.profile"; do
    strip_rc_block "$rc"
done

# ── Step 9: Restore power profile (Linux only) ───────────────────────────────

next_step "Restoring power profile"

if command -v powerprofilesctl &>/dev/null; then
    _current="$(powerprofilesctl get 2>/dev/null || echo unknown)"
    if [ "$_current" = "performance" ]; then
        info "Resetting power profile from 'performance' → 'balanced'"
        if [ "$DRY_RUN" = "1" ]; then
            printf "  ${DIM}[dry-run]${NC} powerprofilesctl set balanced\n"
        else
            if powerprofilesctl set balanced 2>/dev/null; then
                ok "Power profile reset to 'balanced'"
            else
                warn "Could not reset power profile (run: sudo powerprofilesctl set balanced)"
            fi
        fi
    else
        detail "Power profile already '$_current' — leaving alone"
    fi
else
    detail "powerprofilesctl not present — skipping"
fi

# ── Step 10: Remove the cloned repository (opt-in) ───────────────────────────

next_step "Removing the cloned repository"

# Only offer to remove the repo when it's at a location we ourselves would have
# cloned to. If the user is running this from inside a dev checkout, leave it
# alone — they almost certainly want to keep that.
_safe_to_remove_repo=0
if [ -n "$INSTALL_DIR" ]; then
    if [ "$INSTALL_DIR" = "$HOME/openmono.ai" ]; then
        _safe_to_remove_repo=1
    elif [ -n "${OPENMONO_HOME:-}" ] && [ "$INSTALL_DIR" = "$OPENMONO_HOME" ]; then
        _safe_to_remove_repo=1
    fi
fi

if [ "$KEEP_REPO" = "1" ]; then
    info "Keeping repository (--keep-repo)"
elif [ "$_safe_to_remove_repo" = "1" ]; then
    if confirm "Remove the cloned repo at $INSTALL_DIR ?" "N"; then
        # We're about to rm -rf the directory we may currently be inside.
        # Step out of it first so the rm doesn't fail on macOS / NFS, and so a
        # post-rm `pwd` doesn't error out.
        cd "$HOME" 2>/dev/null || cd /
        rm_path "$INSTALL_DIR" "repository at $INSTALL_DIR"
    else
        info "Keeping repository"
    fi
else
    info "Repository at $INSTALL_DIR appears to be a dev checkout — leaving alone."
    info "To remove manually: rm -rf $INSTALL_DIR"
fi

# ── Tail summary if NOT --deep (no step number — this is the closing banner) ──

if [ "$DEEP" != "1" ]; then
    echo ""
    printf "${GREEN}${BOLD}%s${NC}\n" "$(printf '═%.0s' $(seq 1 60))"
    printf "${GREEN}${BOLD}  ✓  OpenMono uninstall complete${NC}\n"
    printf "${GREEN}${BOLD}%s${NC}\n" "$(printf '═%.0s' $(seq 1 60))"
    echo ""
    printf "  ${DIM}System packages (docker, .NET, NVIDIA stack, etc.) were left in place.${NC}\n"
    printf "  ${DIM}Re-run with ${BOLD}--deep${NC}${DIM} to also remove those.${NC}\n"
    echo ""
    if [ "$DRY_RUN" != "1" ]; then
        show_log_location
    fi
    exit 0
fi

# ────────────────────────────────────────────────────────────────────────────
# --deep mode: reverse install_prereqs.sh
# ────────────────────────────────────────────────────────────────────────────

if [ "$IS_UBUNTU" != "1" ]; then
    warn "--deep only reverses Ubuntu apt installs from install_prereqs.sh."
    warn "Detected platform: $PLATFORM. Skipping apt steps."

    next_step "Removing .NET 10 SDK at \$HOME/.dotnet"
    if [ "$KEEP_DOTNET" = "1" ]; then
        info "Keeping .NET SDK (--keep-dotnet)"
    elif [ -d "$HOME/.dotnet" ]; then
        _dotnet_size="$(du -sh "$HOME/.dotnet" 2>/dev/null | cut -f1)"
        if confirm "Remove \$HOME/.dotnet (~$_dotnet_size)?" "Y"; then
            rm_path "$HOME/.dotnet" "\$HOME/.dotnet"
        else
            info "Keeping .NET SDK"
        fi
    else
        detail "\$HOME/.dotnet not present — skipping"
    fi

    next_step "Uninstalling pip --user packages"
    pip_uninstall code-review-graph
    pip_uninstall graphifyy
    pip_uninstall graphify
    echo ""
    ok "Deep-uninstall complete (non-Ubuntu fast path)"
    echo ""
    [ "$DRY_RUN" != "1" ] && show_log_location
    exit 0
fi

if [ -z "$SUDO" ] && [ "$(id -u)" -ne 0 ]; then
    err "--deep requires sudo on Ubuntu but no sudo is available."
    die "Cannot remove system packages without root."
fi

# ── Step 11: Remove pip --user packages ──────────────────────────────────────

next_step "Uninstalling pip --user packages"
pip_uninstall code-review-graph
# install.sh installs the package as 'graphifyy' (note the double-y); reflect that.
pip_uninstall graphifyy
pip_uninstall graphify  # defensive: older versions may have used this name

# ── Step 12: Remove Docker (apt) ─────────────────────────────────────────────

next_step "Removing Docker (apt packages + repo + keyring)"

if [ "$KEEP_DOCKER" = "1" ]; then
    info "Keeping Docker (--keep-docker)"
else
    if confirm "Remove docker-ce + plugins + apt repo? This deletes ALL local Docker state." "N"; then
        for pkg in docker-ce docker-ce-cli docker-buildx-plugin docker-compose-plugin containerd.io; do
            uninstall_apt_pkg "$pkg"
        done
        do_run $SUDO apt-get autoremove -y -qq

        rm_path "/etc/apt/sources.list.d/docker.list" "Docker apt source"
        rm_path "/etc/apt/keyrings/docker.gpg"        "Docker apt keyring"

        if confirm "Also delete /var/lib/docker (ALL images, volumes, containers)?" "N"; then
            do_run $SUDO rm -rf /var/lib/docker /var/lib/containerd
            ok "Docker data directories removed"
        else
            info "Keeping /var/lib/docker — re-install will pick it up"
        fi

        # Drop the docker group entirely only if it's empty — it might be in
        # use by other software (e.g. portainer, podman compat shims).
        if getent group docker &>/dev/null && [ -z "$(getent group docker | cut -d: -f4)" ]; then
            do_run $SUDO groupdel docker
            ok "Empty 'docker' group removed"
        else
            detail "docker group has members or is absent — leaving it alone"
        fi
    else
        info "Keeping Docker"
    fi
fi

# ── Step 13: Remove NVIDIA stack ─────────────────────────────────────────────

next_step "Removing NVIDIA stack (drivers + CUDA + container toolkit)"

if [ "$KEEP_NVIDIA" = "1" ]; then
    info "Keeping NVIDIA stack (--keep-nvidia)"
elif ! command -v dpkg &>/dev/null; then
    detail "dpkg not available — skipping"
else
    # Only offer NVIDIA removal if any NVIDIA package is actually present —
    # otherwise the prompt is noise on a CPU-only host.
    if dpkg -l 2>/dev/null | grep -qE 'nvidia-(driver|container|cuda|utils)' ; then
        warn "NVIDIA driver removal affects ALL applications using the GPU on this host."
        warn "Display managers may fall back to nouveau / llvmpipe after this step."
        if confirm "Remove NVIDIA drivers, CUDA, and container toolkit?" "N"; then
            # Container toolkit first — it depends on the driver, not the other way.
            uninstall_apt_pkg nvidia-container-toolkit
            uninstall_apt_pkg nvidia-container-toolkit-base
            uninstall_apt_pkg libnvidia-container-tools
            uninstall_apt_pkg libnvidia-container1

            rm_path "/etc/apt/sources.list.d/nvidia-container-toolkit.list" "nvidia-container-toolkit apt source"
            rm_path "/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg" "nvidia-container-toolkit keyring"

            uninstall_apt_pkg nvidia-cuda-toolkit
            uninstall_apt_pkg nvidia-cuda-toolkit-doc

            # Driver / utils — names vary by version. Glob via dpkg.
            for pkg in $(dpkg -l 2>/dev/null | awk '/^ii\s+nvidia-(driver|utils|dkms|kernel)/ {print $2}'); do
                uninstall_apt_pkg "$pkg"
            done
            for pkg in $(dpkg -l 2>/dev/null | awk '/^ii\s+(libnvidia|libcuda)/ {print $2}'); do
                uninstall_apt_pkg "$pkg"
            done

            uninstall_apt_pkg ubuntu-drivers-common

            do_run $SUDO apt-get autoremove -y -qq
            warn "A reboot is recommended to fully unload the NVIDIA kernel module."
        else
            info "Keeping NVIDIA stack"
        fi
    else
        detail "No NVIDIA packages installed — skipping"
    fi
fi

# ── Step 14: Remove .NET SDK ─────────────────────────────────────────────────

next_step "Removing .NET 10 SDK"

if [ "$KEEP_DOTNET" = "1" ]; then
    info "Keeping .NET SDK (--keep-dotnet)"
elif [ -d "$HOME/.dotnet" ]; then
    _dotnet_size="$(du -sh "$HOME/.dotnet" 2>/dev/null | cut -f1)"
    if confirm "Remove \$HOME/.dotnet (~$_dotnet_size)?" "Y"; then
        rm_path "$HOME/.dotnet" "\$HOME/.dotnet"
    else
        info "Keeping .NET SDK"
    fi
else
    detail "\$HOME/.dotnet not present — skipping"
fi

# ── Done ──────────────────────────────────────────────────────────────────────

echo ""
printf "${GREEN}${BOLD}%s${NC}\n" "$(printf '═%.0s' $(seq 1 60))"
printf "${GREEN}${BOLD}  ✓  Deep uninstall complete${NC}\n"
printf "${GREEN}${BOLD}%s${NC}\n" "$(printf '═%.0s' $(seq 1 60))"
echo ""
printf "  ${DIM}System tools NOT removed (intentional):${NC}\n"
printf "  ${DIM}  git, curl, jq, cmake, ripgrep, build-essential, python3-pip,${NC}\n"
printf "  ${DIM}  libopenblas-dev, pkg-config, pciutils, ca-certificates, gnupg.${NC}\n"
printf "  ${DIM}Remove those manually if you really need to.${NC}\n"
echo ""

# A new shell will be needed for the .NET PATH stripping to take effect.
if [ -d "$HOME/.dotnet" ]; then
    : # still installed, no shell-reload note needed
elif grep -q "DOTNET_ROOT" "$HOME/.bashrc" "$HOME/.zshrc" "$HOME/.profile" 2>/dev/null; then
    : # some rc still has it — strip_rc_block failed quietly somewhere
else
    info "Open a new shell to pick up the updated PATH (.NET was on it)."
fi

if [ "$DRY_RUN" != "1" ]; then
    show_log_location
fi
