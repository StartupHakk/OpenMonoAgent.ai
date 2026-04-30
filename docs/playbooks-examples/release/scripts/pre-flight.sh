#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# release/scripts/pre-flight.sh
#
# Pre-flight validation for the release playbook.
# Checks: Docker daemon, .NET SDK, clean git state, no merge conflicts,
#         required CLI tools present, and branch guardrails.
#
# Exit 0 = all checks passed.
# Exit 1 = one or more checks failed (details printed to stderr).
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

PASS=0
FAIL=1
errors=()
warnings=()

# ── Colour helpers ─────────────────────────────────────────────────────────────
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

ok()   { printf "  ${GREEN}✔${NC}  %s\n" "$1"; }
warn() { printf "  ${YELLOW}⚠${NC}  %s\n" "$1"; warnings+=("$1"); }
fail() { printf "  ${RED}✘${NC}  %s\n" "$1" >&2; errors+=("$1"); }
sep()  { printf "${BOLD}%s${NC}\n" "──────────────────────────────────────────"; }

printf "\n${BOLD}Pre-flight checks — OpenMono.ai Release${NC}\n\n"

# ── 1. Required CLI tools ──────────────────────────────────────────────────────
sep
printf "${BOLD}[1/6] Required tools${NC}\n"

for cmd in git dotnet docker curl jq; do
    if command -v "$cmd" &>/dev/null; then
        ok "$cmd: $(command -v "$cmd")"
    else
        fail "$cmd is not installed or not on PATH"
    fi
done

# ── 2. .NET SDK version ────────────────────────────────────────────────────────
sep
printf "${BOLD}[2/6] .NET SDK${NC}\n"

if command -v dotnet &>/dev/null; then
    SDK_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
    MAJOR=${SDK_VERSION%%.*}
    if [[ "$MAJOR" -ge 8 ]] 2>/dev/null; then
        ok ".NET SDK $SDK_VERSION"
    else
        fail ".NET SDK $SDK_VERSION is below minimum required (8.0)"
    fi
fi

# ── 3. Docker daemon ───────────────────────────────────────────────────────────
sep
printf "${BOLD}[3/6] Docker daemon${NC}\n"

if docker info &>/dev/null 2>&1; then
    DOCKER_VERSION=$(docker version --format '{{.Server.Version}}' 2>/dev/null || echo "unknown")
    ok "Docker daemon running (server $DOCKER_VERSION)"
else
    warn "Docker daemon is not running — Docker-related steps will be skipped"
fi

# ── 4. Git repository state ────────────────────────────────────────────────────
sep
printf "${BOLD}[4/6] Git repository state${NC}\n"

# Must be inside a git repository
if ! git rev-parse --git-dir &>/dev/null 2>&1; then
    fail "Not inside a git repository"
else
    ok "Git repository detected"

    # Current branch
    BRANCH=$(git symbolic-ref --short HEAD 2>/dev/null || git rev-parse --short HEAD)
    ok "Current branch: $BRANCH"

    # Guard against releasing from non-main branches
    if [[ "$BRANCH" != "main" && "$BRANCH" != "master" ]]; then
        warn "Releasing from branch '$BRANCH' — expected 'main' or 'master'"
    fi

    # Clean working tree
    if [[ -n "$(git status --porcelain)" ]]; then
        DIRTY_COUNT=$(git status --porcelain | wc -l | tr -d ' ')
        fail "Working tree is dirty ($DIRTY_COUNT modified/untracked files). Commit or stash before releasing."
        git status --short | head -20 | sed 's/^/     /' >&2
    else
        ok "Working tree is clean"
    fi

    # No merge in progress
    GIT_DIR=$(git rev-parse --git-dir)
    if [[ -f "$GIT_DIR/MERGE_HEAD" ]]; then
        fail "A merge is in progress. Resolve it before releasing."
    else
        ok "No merge in progress"
    fi

    # No rebase in progress
    if [[ -d "$GIT_DIR/rebase-merge" || -d "$GIT_DIR/rebase-apply" ]]; then
        fail "A rebase is in progress. Resolve it before releasing."
    else
        ok "No rebase in progress"
    fi

    # Check remote is reachable
    REMOTE_URL=$(git remote get-url origin 2>/dev/null || echo "")
    if [[ -n "$REMOTE_URL" ]]; then
        if git ls-remote --exit-code origin HEAD &>/dev/null 2>&1; then
            ok "Remote 'origin' is reachable"
        else
            warn "Remote 'origin' is unreachable — push steps will fail"
        fi
    else
        warn "No remote 'origin' configured"
    fi

    # Confirm HEAD is pushed (not ahead of remote)
    AHEAD=$(git rev-list --count @{u}..HEAD 2>/dev/null || echo "?")
    if [[ "$AHEAD" == "0" ]]; then
        ok "Branch is up-to-date with remote"
    elif [[ "$AHEAD" == "?" ]]; then
        warn "Could not determine ahead/behind status (no upstream tracking)"
    else
        warn "Branch is $AHEAD commit(s) ahead of remote — push before releasing"
    fi
fi

# ── 5. Solution / project files ────────────────────────────────────────────────
sep
printf "${BOLD}[5/6] Solution structure${NC}\n"

SLN_FILES=($(find . -maxdepth 2 -name "*.sln" 2>/dev/null))
if [[ ${#SLN_FILES[@]} -eq 0 ]]; then
    fail "No .sln file found in workspace"
else
    ok "Solution: ${SLN_FILES[0]}"
fi

CSPROJ_COUNT=$(find . -name "*.csproj" 2>/dev/null | wc -l | tr -d ' ')
ok "Project files: $CSPROJ_COUNT .csproj file(s) found"

# ── 6. Secret / credential file scan ──────────────────────────────────────────
sep
printf "${BOLD}[6/6] Secret file scan${NC}\n"

DANGEROUS_PATTERNS=(".env" "*.pem" "*.key" "*.p12" "*.pfx" "credentials.json" "secrets.json")
FOUND_SECRETS=false

for pattern in "${DANGEROUS_PATTERNS[@]}"; do
    MATCHES=$(git ls-files "$pattern" 2>/dev/null || true)
    if [[ -n "$MATCHES" ]]; then
        fail "Tracked secret-like file detected: $MATCHES"
        FOUND_SECRETS=true
    fi
done

if [[ "$FOUND_SECRETS" == false ]]; then
    ok "No tracked secret files detected"
fi

# ── Summary ────────────────────────────────────────────────────────────────────
sep
echo ""

if [[ ${#errors[@]} -gt 0 ]]; then
    printf "${RED}${BOLD}Pre-flight FAILED — %d error(s)${NC}\n\n" "${#errors[@]}"
    for err in "${errors[@]}"; do
        printf "  ${RED}✘${NC} %s\n" "$err"
    done
    echo ""
    exit "$FAIL"
fi

if [[ ${#warnings[@]} -gt 0 ]]; then
    printf "${YELLOW}Pre-flight passed with %d warning(s)${NC}\n\n" "${#warnings[@]}"
    for w in "${warnings[@]}"; do
        printf "  ${YELLOW}⚠${NC} %s\n" "$w"
    done
else
    printf "${GREEN}${BOLD}Pre-flight passed — all checks OK${NC}\n"
fi

echo ""
exit "$PASS"
