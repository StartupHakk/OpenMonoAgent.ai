#!/usr/bin/env bash
# Opens an SSH session to Mac mini (3) and prints LLAMA_API_KEY.
set -euo pipefail

MINI_HOST="${MINI_HOST:-100.84.136.111}"
MINI_USER="${MINI_USER:-abc}"

cat <<EOF
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Fetch LLAMA_API_KEY from Mac mini (3)
  Host: ${MINI_USER}@${MINI_HOST}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

You will be asked for the Mini's login password.
Then we print LLAMA_API_KEY from docker/.env (or settings).

EOF

ssh -t "${MINI_USER}@${MINI_HOST}" bash -s <<'REMOTE'
set +e
echo ""
echo "HOSTNAME: $(hostname)"
echo "ARCH:     $(uname -m)"
echo ""
echo "--- Searching for LLAMA_API_KEY ---"
FOUND=0
for f in \
  "$HOME/openmono.ai/docker/.env" \
  "$HOME/Downloads/OpenMonoAgent.ai-macOS-inference-support/docker/.env" \
  "$HOME/Downloads/OpenMonoAgent.ai/docker/.env" \
  "./docker/.env"
do
  if [[ -f "$f" ]] && grep -q '^LLAMA_API_KEY=' "$f" 2>/dev/null; then
    echo "FILE: $f"
    grep '^LLAMA_API_KEY=' "$f"
    FOUND=1
  fi
done

if [[ "$FOUND" -eq 0 ]]; then
  echo "(no docker/.env key found)"
  echo ""
  echo "Try on this Mini:"
  echo "  cd ~/openmono.ai && ./openmono tunnel setup --tailscale"
fi

echo ""
echo "--- settings.json keys (if any) ---"
python3 - <<'PY'
import json
from pathlib import Path
p = Path.home() / ".openmono" / "settings.json"
if not p.exists():
    print("(no ~/.openmono/settings.json)")
else:
    d = json.loads(p.read_text() or "{}")
    inf = (d.get("inference") or {}).get("api_key") or ""
    llm = (d.get("llm") or {}).get("api_key") or ""
    print("inference.api_key:", inf if inf else "(empty)")
    print("llm.api_key:      ", llm if llm else "(empty)")
PY

echo ""
echo "--- Done. Copy the LLAMA_API_KEY=... value ---"
echo "On the iMac, run:"
echo "  cd ~/Downloads/OpenMonoAgent.ai-macOS-inference-support"
echo "  ./scripts/apply-mini-endpoint.sh YOUR_KEY_HERE"
echo ""
exec bash -l
REMOTE
