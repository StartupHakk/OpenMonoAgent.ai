#!/usr/bin/env bash
# incident-response/scripts/verify-recovery.sh
# Checks common health endpoints. Exit 0 = healthy, exit 1 = still failing.
set -euo pipefail

SERVICE="${SERVICE:-unknown}"
PASS=0
FAIL=0

ok()   { printf "  ✔  %s\n" "$1"; PASS=$((PASS+1)); }
fail() { printf "  ✘  %s\n" "$1" >&2; FAIL=$((FAIL+1)); }

echo "Recovery verification — service: $SERVICE"
echo "──────────────────────────────────────────"

# Try common health endpoints
for url in \
  "http://localhost/health" \
  "http://localhost:8080/health" \
  "http://localhost:3000/health" \
  "http://localhost:5000/health"; do
  if curl -sf --max-time 5 "$url" &>/dev/null; then
    ok "Health endpoint $url → 200 OK"
    break
  fi
done

# Check if service process is running
if pgrep -f "$SERVICE" &>/dev/null 2>&1; then
  ok "Process '$SERVICE' is running"
else
  fail "Process '$SERVICE' not found"
fi

echo ""
if [ "$FAIL" -gt 0 ]; then
  echo "Recovery NOT confirmed — $FAIL check(s) failed."
  exit 1
fi

echo "Recovery confirmed — all checks passed."
exit 0
