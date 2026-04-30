#!/usr/bin/env bash
# db-migrate/scripts/smoke-test.sh
# Basic smoke tests after migration. Exit 1 if any critical check fails.
set -euo pipefail

ENV="${1:-staging}"
PASS=0
FAIL=0

ok()   { printf "  ✔  %s\n" "$1"; PASS=$((PASS+1)); }
fail() { printf "  ✘  %s\n" "$1" >&2; FAIL=$((FAIL+1)); }

echo "Smoke tests — $ENV"
echo "──────────────────────────────────────────"

# Detect app URL based on environment
if [ "$ENV" = "staging" ]; then
  APP_URL="${APP_URL_STAGING:-http://localhost:8080}"
else
  APP_URL="${APP_URL_PROD:-http://localhost:8080}"
fi

# Health endpoint
if curl -sf --max-time 10 "$APP_URL/health" &>/dev/null; then
  ok "Health endpoint: $APP_URL/health → 200"
elif curl -sf --max-time 10 "$APP_URL/healthz" &>/dev/null; then
  ok "Health endpoint: $APP_URL/healthz → 200"
else
  fail "Health endpoint unreachable at $APP_URL"
fi

# Database connectivity via app
if curl -sf --max-time 10 "$APP_URL/ready" &>/dev/null; then
  ok "Readiness probe → 200"
fi

echo ""
if [ "$FAIL" -gt 0 ]; then
  echo "Smoke tests FAILED — $FAIL check(s) failed. Do not proceed to production."
  exit 1
fi

echo "Smoke tests passed ($PASS checks)."
exit 0
