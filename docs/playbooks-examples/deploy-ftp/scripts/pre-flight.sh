#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# deploy-ftp/scripts/pre-flight.sh
#
# Pre-flight checks for the FTP deploy playbook.
# Checks: required tools, FTP credentials in environment, local path readable.
#
# Exit 0 = all checks passed.
# Exit 1 = one or more checks failed.
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

errors=()
warnings=()

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

ok()   { printf "  ${GREEN}✔${NC}  %s\n" "$1"; }
warn() { printf "  ${YELLOW}⚠${NC}  %s\n" "$1"; warnings+=("$1"); }
fail() { printf "  ${RED}✘${NC}  %s\n" "$1" >&2; errors+=("$1"); }
sep()  { printf "${BOLD}%s${NC}\n" "──────────────────────────────────────────"; }

printf "\n${BOLD}Pre-flight checks — FTP Deploy${NC}\n\n"

# ── 1. Required tools ──────────────────────────────────────────────────────────
sep
printf "${BOLD}[1/3] Required tools${NC}\n"

if command -v lftp &>/dev/null; then
    ok "lftp: $(command -v lftp)"
else
    fail "lftp is not installed. Install it with: brew install lftp  |  apt install lftp"
fi

if command -v find &>/dev/null; then
    ok "find: available"
fi

# ── 2. FTP credentials ─────────────────────────────────────────────────────────
sep
printf "${BOLD}[2/3] FTP credentials${NC}\n"

if [[ -z "${FTP_PASSWORD:-}" ]]; then
    fail "FTP_PASSWORD environment variable is not set. Export it before running the playbook:
       export FTP_PASSWORD='your-password'"
else
    ok "FTP_PASSWORD is set (value hidden)"
fi

if [[ -z "${FTP_HOST:-}" ]]; then
    warn "FTP_HOST not set as env var — will use the value from playbook parameters"
else
    ok "FTP_HOST: ${FTP_HOST}"
fi

if [[ -z "${FTP_USER:-}" ]]; then
    warn "FTP_USER not set as env var — will use the value from playbook parameters"
else
    ok "FTP_USER: ${FTP_USER}"
fi

# ── 3. Local path ──────────────────────────────────────────────────────────────
sep
printf "${BOLD}[3/3] Local build path${NC}\n"

LOCAL_PATH="${LOCAL_PATH:-./dist}"

if [[ -d "$LOCAL_PATH" ]]; then
    FILE_COUNT=$(find "$LOCAL_PATH" -type f | wc -l | tr -d ' ')
    ok "Local path exists: $LOCAL_PATH ($FILE_COUNT files)"
    if [[ "$FILE_COUNT" -eq 0 ]]; then
        warn "Local path is empty — run your build command first, or it will run in the build step"
    fi
else
    warn "Local path does not exist yet: $LOCAL_PATH — the build step will create it"
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
    exit 1
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
exit 0
