#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# release/scripts/validate-tests.sh
#
# Runs `dotnet test` across the entire solution, parses the results, and
# reports a structured pass/fail summary.
#
# Exit 0 = all tests passed (or no tests found).
# Exit 1 = one or more tests failed or the build failed.
#
# Environment:
#   TEST_FILTER    Optional: dotnet test --filter expression (e.g. "Category=unit")
#   TEST_VERBOSITY dotnet test verbosity (default: minimal)
#   BUILD_CONFIG   Build configuration to test (default: Release)
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

BUILD_CONFIG="${BUILD_CONFIG:-Release}"
VERBOSITY="${TEST_VERBOSITY:-minimal}"
FILTER_EXPR="${TEST_FILTER:-}"
LOG_FILE=$(mktemp /tmp/openmono-test-XXXXXX.log)
RESULT_FILE=$(mktemp /tmp/openmono-test-results-XXXXXX.trx)

cleanup() { rm -f "$LOG_FILE" "$RESULT_FILE"; }
trap cleanup EXIT

printf "\n${BOLD}Test Validation — OpenMono.ai Release${NC}\n\n"

# ── Locate solution ────────────────────────────────────────────────────────────
SLN=$(find . -maxdepth 2 -name "*.sln" 2>/dev/null | head -1)
if [[ -z "$SLN" ]]; then
    printf "${RED}✘ No .sln file found. Cannot run tests.${NC}\n"
    exit 1
fi
printf "  Solution : %s\n" "$SLN"
printf "  Config   : %s\n" "$BUILD_CONFIG"
printf "  Filter   : %s\n" "${FILTER_EXPR:-<none>}"
echo ""

# ── Step 1: Build in Release mode ─────────────────────────────────────────────
printf "${BOLD}[1/2] Building solution...${NC}\n"

BUILD_ARGS=(dotnet build "$SLN" --configuration "$BUILD_CONFIG" --no-incremental -v quiet)

if ! "${BUILD_ARGS[@]}" > "$LOG_FILE" 2>&1; then
    printf "${RED}✘ Build failed.${NC}\n\n"
    tail -30 "$LOG_FILE" | sed 's/^/  /'
    exit 1
fi

# Count warnings from build
WARN_COUNT=$(grep -c ": warning " "$LOG_FILE" 2>/dev/null || echo 0)
printf "  ${GREEN}✔${NC}  Build succeeded"
[[ "$WARN_COUNT" -gt 0 ]] && printf " (${YELLOW}%d warning(s)${NC})" "$WARN_COUNT"
printf "\n\n"

# ── Step 2: Run tests ──────────────────────────────────────────────────────────
printf "${BOLD}[2/2] Running tests...${NC}\n"

TEST_ARGS=(
    dotnet test "$SLN"
    --configuration "$BUILD_CONFIG"
    --no-build
    --verbosity "$VERBOSITY"
    --logger "trx;LogFileName=$RESULT_FILE"
    --results-directory /tmp
)

[[ -n "$FILTER_EXPR" ]] && TEST_ARGS+=(--filter "$FILTER_EXPR")

START_TS=$(date +%s)
"${TEST_ARGS[@]}" > "$LOG_FILE" 2>&1
EXIT_CODE=$?
END_TS=$(date +%s)
ELAPSED=$(( END_TS - START_TS ))

# ── Parse results from log ─────────────────────────────────────────────────────
PASSED=$(grep -oP 'Passed:\s*\K\d+' "$LOG_FILE" 2>/dev/null | tail -1 || echo 0)
FAILED=$(grep -oP 'Failed:\s*\K\d+' "$LOG_FILE" 2>/dev/null | tail -1 || echo 0)
SKIPPED=$(grep -oP 'Skipped:\s*\K\d+' "$LOG_FILE" 2>/dev/null | tail -1 || echo 0)
TOTAL=$(grep -oP 'Total:\s*\K\d+' "$LOG_FILE" 2>/dev/null | tail -1 || echo 0)

echo ""
printf "  %-12s %s\n" "Passed:"  "${PASSED}"
printf "  %-12s %s\n" "Failed:"  "${FAILED}"
printf "  %-12s %s\n" "Skipped:" "${SKIPPED}"
printf "  %-12s %s\n" "Total:"   "${TOTAL}"
printf "  %-12s %ds\n" "Duration:" "$ELAPSED"
echo ""

# ── List failing tests if any ──────────────────────────────────────────────────
if [[ "${FAILED}" -gt 0 ]] 2>/dev/null || [[ "$EXIT_CODE" -ne 0 ]]; then
    printf "${RED}${BOLD}✘ Tests FAILED${NC}\n\n"

    # Extract failing test names from log (dotnet test prints them with "Failed" prefix)
    FAILING=$(grep -E "^\s*(Failed|Error)\s" "$LOG_FILE" 2>/dev/null \
              | sed 's/^ */  /' \
              | head -30 || true)
    if [[ -n "$FAILING" ]]; then
        printf "Failing tests:\n%s\n\n" "$FAILING"
    fi

    # Show last 40 lines of full log for context
    printf "Last output:\n"
    tail -40 "$LOG_FILE" | sed 's/^/  /'
    echo ""

    exit 1
fi

printf "${GREEN}${BOLD}✔ All tests passed${NC} (%s/%s in %ds)\n\n" "$PASSED" "$TOTAL" "$ELAPSED"
exit 0
