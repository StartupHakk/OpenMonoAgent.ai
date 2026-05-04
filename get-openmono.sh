#!/usr/bin/env bash
set -euo pipefail

# ────────────────────────────────────────────────────────────────────────────────
# OpenMono.ai – bootstrap installer
# ────────────────────────────────────────────────────────────────────────────────

REPO_URL="https://github.com/StartupHakk/OpenMonoAgent.ai.git"
INSTALL_DIR="${OPENMONO_HOME:-$HOME/openmono.ai}"

BLUE='\033[38;2;163;255;102m'
GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

info() { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
err()  { echo -e "${RED}[ERROR]${NC} $*" >&2; }
die()  { err "$*"; exit 1; }

# ── Preflight checks ──────────────────────────────────────────────────────────

command -v git  &>/dev/null || die "git is required – install it first: sudo apt install git"
command -v curl &>/dev/null || die "curl is required – install it first: sudo apt install curl"

# ── Clone or update ───────────────────────────────────────────────────────────

if [ -d "$INSTALL_DIR/.git" ]; then
    info "Repository already exists at $INSTALL_DIR – pulling latest..."
    git -C "$INSTALL_DIR" pull --ff-only 2>/dev/null \
        || info "Could not fast-forward; continuing with existing checkout"
else
    info "Cloning OpenMono.ai to $INSTALL_DIR..."
    git clone "$REPO_URL" "$INSTALL_DIR" || die "git clone failed"
fi

ok "Repository ready at $INSTALL_DIR"

# ── Make CLI executable ───────────────────────────────────────────────────────

chmod +x "$INSTALL_DIR/openmono" "$INSTALL_DIR/scripts/"*.sh

# ── Hand off to openmono setup (passes all flags through) ────────────────────

exec "$INSTALL_DIR/openmono" setup "$@"
