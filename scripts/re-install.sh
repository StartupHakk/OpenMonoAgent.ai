#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai Re-Installer
#
# Pulls the latest changes from the public GitHub repository and rebuilds
# ONLY the .NET-based agent Docker container. The llama-server (llama.cpp)
# container is intentionally left untouched.
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

TOTAL_STEPS=3

banner "OpenMono.ai Re-Installer"

# ── Step 1: Resolve install directory ─────────────────────────────────────────

step 1 $TOTAL_STEPS "Resolving install directory"

if [ -f "$SCRIPT_DIR/../OpenMono.sln" ]; then
    INSTALL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
elif [ -n "${OPENMONO_HOME:-}" ]; then
    INSTALL_DIR="$OPENMONO_HOME"
else
    INSTALL_DIR="$HOME/openmono.ai"
fi

[ -f "$INSTALL_DIR/OpenMono.sln" ] \
    || die "OpenMono repository not found at $INSTALL_DIR"

ok "Install directory: $INSTALL_DIR"
cd "$INSTALL_DIR"

if ! command -v docker &>/dev/null; then
    die "docker is required but not installed. Run: openmono setup"
fi
if ! docker compose version &>/dev/null 2>&1; then
    die "Docker Compose is required. Run: openmono setup"
fi

# ── Step 2: Pull latest changes ───────────────────────────────────────────────

step 2 $TOTAL_STEPS "Pulling latest changes"

if [ -n "$(git status --porcelain)" ]; then
    die "Local changes in $INSTALL_DIR would block a clean update. Stash or discard them, then retry: git stash"
fi

if ! run git pull --ff-only; then
    die "git pull failed (check network connectivity and retry)"
fi

ok "Repository updated"

# ── Step 3: Rebuild .NET agent container (skip llama-server) ──────────────────

step 3 $TOTAL_STEPS "Rebuilding .NET agent container (llama-server is skipped)"

cd "$INSTALL_DIR/docker"

info "Stopping any running agent container..."
run docker compose stop agent || true
run docker compose rm -f agent || true

info "Clearing Docker build cache for a clean agent rebuild..."
docker system prune --all --force || true

info "Building agent image..."
if ! run docker compose build agent; then
    die "agent build failed"
fi

ok "Agent image rebuilt"

# ── Done ──────────────────────────────────────────────────────────────────────

echo ""
printf "${GREEN}${BOLD}%s${NC}\n" "$(printf '═%.0s' $(seq 1 60))"
printf "${GREEN}${BOLD}  ✓  Re-install Complete${NC}\n"
printf "${GREEN}${BOLD}%s${NC}\n" "$(printf '═%.0s' $(seq 1 60))"
echo ""
printf "  ${GREEN}✓${NC}  agent (.NET)      rebuilt\n"
printf "  ${GREEN}✓${NC}  llama-server      left untouched\n"
echo ""
printf "  ${BOLD}Next steps:${NC}\n"
printf "  ${GREEN}1.${NC}  cd your-project/\n"
printf "  ${GREEN}2.${NC}  openmono agent\n"
echo ""
show_log_location
