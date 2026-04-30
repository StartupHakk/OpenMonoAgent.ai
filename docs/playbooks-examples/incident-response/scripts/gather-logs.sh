#!/usr/bin/env bash
# incident-response/scripts/gather-logs.sh
# Validates log sources are accessible. Exit 0 = at least one source found.
set -euo pipefail

SERVICE="${SERVICE:-unknown}"
FOUND=0

check() { printf "  ✔  %s\n" "$1"; FOUND=$((FOUND+1)); }
miss()  { printf "  -  %s\n" "$1"; }

echo "Log source discovery — service: $SERVICE"
echo "──────────────────────────────────────────"

[ -f "/var/log/$SERVICE/error.log" ]           && check "/var/log/$SERVICE/error.log"    || miss "/var/log/$SERVICE/error.log"
command -v journalctl &>/dev/null              && check "journalctl (systemd)"            || miss "journalctl"
command -v docker &>/dev/null && \
  docker ps --format '{{.Names}}' 2>/dev/null | grep -q "$SERVICE" \
                                               && check "docker logs $SERVICE"            || miss "docker logs"
[ -f "logs/error.log" ]                        && check "logs/error.log (local)"         || miss "logs/error.log"

echo ""
if [ "$FOUND" -eq 0 ]; then
  echo "WARNING: No log sources found for '$SERVICE'. Proceeding with manual investigation."
  exit 0
fi

echo "Found $FOUND log source(s)."
exit 0
