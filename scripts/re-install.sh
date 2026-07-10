#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai Re-Installer
#
# Pulls the latest changes from GitLab (prompting for credentials) and
# rebuilds ONLY the .NET-based agent Docker container. The llama-server
# (llama.cpp) container is intentionally left untouched.
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

TOTAL_STEPS=4

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

# ── Local-changes guard ───────────────────────────────────────────────────────
# Never let an update roll back local modifications (e.g. a customized Vulkan
# lane). Abort before pulling if the working tree is dirty or the current
# branch carries commits that upstream doesn't have.
# Override intentionally with: OPENMONO_FORCE_UPDATE=1 scripts/re-install.sh

if [ "${OPENMONO_FORCE_UPDATE:-0}" != "1" ]; then
    if [ -n "$(git status --porcelain=v1 --untracked-files=no 2>/dev/null)" ]; then
        err "Local uncommitted changes detected — update aborted to protect them."
        err "Review them with:  git status"
        err "Then either commit them (git add -A && git commit) or, to update anyway:"
        die "  OPENMONO_FORCE_UPDATE=1 $0"
    fi
    _CURRENT_BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
    _LOCAL_ONLY="$(git rev-list --count "origin/main..HEAD" 2>/dev/null || echo 0)"
    if [ "$_CURRENT_BRANCH" != "main" ] || [ "${_LOCAL_ONLY:-0}" -gt 0 ]; then
        err "You are on branch '$_CURRENT_BRANCH' with ${_LOCAL_ONLY:-?} local commit(s) not in origin/main."
        err "Updating would require merging/rebasing your local work — this is not done automatically."
        err "To update anyway (your commits are kept, conflicts must be resolved manually):"
        die "  OPENMONO_FORCE_UPDATE=1 $0"
    fi
fi

# ── Step 2: Prompt for GitLab credentials ─────────────────────────────────────

step 2 $TOTAL_STEPS "GitLab credentials"

printf "  GitLab username: "
read -r GIT_USER
[ -n "$GIT_USER" ] || die "username cannot be empty"

printf "  GitLab password (or personal access token): "
read -r -s GIT_PASS
echo ""
[ -n "$GIT_PASS" ] || die "password cannot be empty"

# ── Step 3: Pull latest changes ───────────────────────────────────────────────

step 3 $TOTAL_STEPS "Pulling latest changes"

# Use GIT_ASKPASS so credentials never end up in process args, the remote URL,
# or git config. The helper script reads from env vars we export only for the
# duration of the pull.
ASKPASS_DIR="$(mktemp -d)"
cleanup() {
    rm -rf "$ASKPASS_DIR"
    unset GIT_USERNAME GIT_PASSWORD
}
trap cleanup EXIT

cat > "$ASKPASS_DIR/askpass.sh" <<'ASKPASS_EOF'
#!/usr/bin/env bash
case "$1" in
    Username*) printf '%s' "$GIT_USERNAME" ;;
    Password*) printf '%s' "$GIT_PASSWORD" ;;
esac
ASKPASS_EOF
chmod +x "$ASKPASS_DIR/askpass.sh"

export GIT_USERNAME="$GIT_USER"
export GIT_PASSWORD="$GIT_PASS"
export GIT_ASKPASS="$ASKPASS_DIR/askpass.sh"
export GIT_TERMINAL_PROMPT=0

if ! run git pull --ff-only; then
    die "git pull failed (check credentials and network)"
fi

unset GIT_USERNAME GIT_PASSWORD
ok "Repository updated"

# ── Step 4: Rebuild .NET agent container (skip llama-server) ──────────────────

step 4 $TOTAL_STEPS "Rebuilding .NET agent container (llama-server is skipped)"

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
