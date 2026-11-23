#!/usr/bin/env bash
# Kill switch: pause all known campaigns for the ai-ads-agents playbook.
# Safe default: dry-run unless --execute is passed.
set -euo pipefail

DRY_RUN=1
CHANNEL="${ADS_CHANNEL:-meta}"
ARTIFACT_DIR="${OPENMONO_ARTIFACTS:-$HOME/.openmono/artifacts/ai-ads-agents}"
mkdir -p "$ARTIFACT_DIR"
LOG="$ARTIFACT_DIR/pause-all-$(date -u +%Y%m%d-%H%M%S).log"

for arg in "$@"; do
  case "$arg" in
    --execute) DRY_RUN=0 ;;
    --dry-run) DRY_RUN=1 ;;
    --channel=*) CHANNEL="${arg#*=}" ;;
  esac
done

{
  echo "UTC: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "channel: $CHANNEL"
  echo "mode: $([[ "$DRY_RUN" -eq 1 ]] && echo DRY_RUN || echo EXECUTE)"
} | tee "$LOG"

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  PAUSE-ALL kill switch ($CHANNEL)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if [[ "$DRY_RUN" -eq 1 ]]; then
  cat <<EOF | tee -a "$LOG"
DRY RUN — no API calls.
To pause for real (when Meta/TikTok/Google tokens are configured):
  ADS_CHANNEL=$CHANNEL $0 --execute

Expected behavior when wired:
  1. List campaign IDs from $ARTIFACT_DIR/paused-campaigns-*.md
  2. Call each platform API to set status=PAUSED
  3. Post SPEND PAUSED to Slack/email if configured
EOF
  exit 0
fi

# --- EXECUTE path (stubs until API credentials exist) ---
if [[ -z "${META_ACCESS_TOKEN:-}" && -z "${TIKTOK_ACCESS_TOKEN:-}" && -z "${GOOGLE_ADS_DEVELOPER_TOKEN:-}" ]]; then
  echo "ERROR: No ad platform tokens in env. Refusing to claim pause." | tee -a "$LOG"
  echo "Set META_ACCESS_TOKEN / TIKTOK_ACCESS_TOKEN / GOOGLE_ADS_* then re-run." | tee -a "$LOG"
  exit 2
fi

echo "TODO: implement platform pause calls for $CHANNEL" | tee -a "$LOG"
echo "Logged: $LOG"
exit 0
