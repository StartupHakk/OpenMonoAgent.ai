#!/usr/bin/env bash
# Scaffold a morning ads brief markdown file.
set -euo pipefail

PRODUCT="${1:-product}"
CHANNEL="${2:-meta}"
DAILY_BUDGET="${3:-50}"
ARTIFACT_DIR="${OPENMONO_ARTIFACTS:-$HOME/.openmono/artifacts/ai-ads-agents}"
mkdir -p "$ARTIFACT_DIR"
OUT="$ARTIFACT_DIR/brief-$(date -u +%Y%m%d).md"

cat > "$OUT" <<EOF
# Morning Ads Brief — ${PRODUCT}

- UTC: $(date -u +%Y-%m-%dT%H:%M:%SZ)
- Channel: ${CHANNEL}
- Daily cap: \$${DAILY_BUDGET}

## Last 24h (fill from APIs / CSV)

| Metric | Value |
|--------|-------|
| Spend | \$ |
| Sales | \$ / orders |
| CAC | \$ |
| ROAS | |

## Winners
- 

## Paused / killed
- 

## Proposed moves (need Approve)
1. 
2. 

## Notes
- AI tested. You decide.
EOF

echo "Wrote $OUT"
echo "$OUT"
