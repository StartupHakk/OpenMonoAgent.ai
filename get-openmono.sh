#!/usr/bin/env bash
set -euo pipefail

# ────────────────────────────────────────────────────────────────────────────────
# OpenMono.ai – bootstrap installer
# ────────────────────────────────────────────────────────────────────────────────

REPO_URL="https://github.com/StartupHakk/OpenMonoAgent.ai.git"
INSTALL_DIR="${OPENMONO_HOME:-$HOME/openmono.ai}"
BRANCH=""

BLUE='\033[38;2;163;255;102m'
GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

info() { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
err()  { echo -e "${RED}[ERROR]${NC} $*" >&2; }
die() { err "$*"; exit 1; }

pkg_install_hint() {
  if command -v dnf &>/dev/null; then echo "sudo dnf install"
  elif command -v yum &>/dev/null; then echo "sudo yum install"
  elif command -v apt &>/dev/null; then echo "sudo apt install"
  elif command -v pacman &>/dev/null; then echo "sudo pacman -S"
  elif command -v zypper &>/dev/null; then echo "sudo zypper install"
  elif command -v brew &>/dev/null; then echo "brew install"
  else echo "your package manager's install command for"
  fi
}

# ── Argument parsing ──────────────────────────────────────────────────────────

PASSTHROUGH_ARGS=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        -b|--branch)
            [[ -n "${2:-}" ]] || die "--branch requires a value"
            BRANCH="$2"; shift 2 ;;
        -*)
            PASSTHROUGH_ARGS+=("$1"); shift ;;
        *)
            # First bare positional arg is treated as an optional branch name
            [[ -z "$BRANCH" ]] && BRANCH="$1" || PASSTHROUGH_ARGS+=("$1")
            shift ;;
    esac
done

# ── Preflight checks ──────────────────────────────────────────────────────────

command -v git &>/dev/null || die "git is required – install it first: $(pkg_install_hint) git"
command -v curl &>/dev/null || die "curl is required – install it first: $(pkg_install_hint) curl"

# ── Clone or update ───────────────────────────────────────────────────────────

BRANCH_LABEL="${BRANCH:-default}"

if [ -d "$INSTALL_DIR/.git" ]; then
    info "Repository already exists at $INSTALL_DIR – fetching latest..."
    git -C "$INSTALL_DIR" fetch --quiet 2>/dev/null || info "Fetch failed; continuing with existing checkout"
    if [[ -n "$BRANCH" ]]; then
        info "Switching to branch '$BRANCH'..."
        git -C "$INSTALL_DIR" checkout "$BRANCH" 2>/dev/null || die "Branch '$BRANCH' not found"
    fi
    git -C "$INSTALL_DIR" pull --ff-only 2>/dev/null \
        || info "Could not fast-forward; continuing with existing checkout"
else
    info "Cloning OpenMono.ai ($BRANCH_LABEL branch) to $INSTALL_DIR..."
    CLONE_ARGS=("$REPO_URL" "$INSTALL_DIR")
    [[ -n "$BRANCH" ]] && CLONE_ARGS=(--branch "$BRANCH" "${CLONE_ARGS[@]}")
    git clone "${CLONE_ARGS[@]}" || die "git clone failed"
fi

ok "Repository ready at $INSTALL_DIR"

# ── Make CLI executable ───────────────────────────────────────────────────────

chmod +x "$INSTALL_DIR/openmono" "$INSTALL_DIR/scripts/"*.sh

# ── Hand off to openmono setup (passes all flags through) ────────────────────

# When piped through curl, stdin is the pipe not the terminal — restore it so
# interactive prompts in `openmono setup` can read user input.
if [[ ! -t 0 ]]; then
    exec "$INSTALL_DIR/openmono" setup "${PASSTHROUGH_ARGS[@]+"${PASSTHROUGH_ARGS[@]}"}" </dev/tty
else
    exec "$INSTALL_DIR/openmono" setup "${PASSTHROUGH_ARGS[@]+"${PASSTHROUGH_ARGS[@]}"}"
fi
