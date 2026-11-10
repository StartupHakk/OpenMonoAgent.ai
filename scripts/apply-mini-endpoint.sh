#!/usr/bin/env bash
# Apply Mac Mini inference endpoint + API key on this agent Mac.
# Usage: ./scripts/apply-mini-endpoint.sh <LLAMA_API_KEY>
set -euo pipefail
KEY="${1:-}"
ENDPOINT="${OPENMONO_ENDPOINT:-http://shawns-mac-mini-3.taild31afe.ts.net:7474}"
if [[ -z "$KEY" ]]; then
  echo "Usage: $0 <LLAMA_API_KEY>"
  echo "Get the key on the Mini: grep LLAMA_API_KEY ~/openmono.ai/docker/.env"
  echo "Or run: openmono tunnel setup --tailscale"
  exit 1
fi
python3 - "$ENDPOINT" "$KEY" <<'PY'
import json, sys
from pathlib import Path
endpoint, key = sys.argv[1], sys.argv[2]
p = Path.home() / ".openmono" / "settings.json"
p.parent.mkdir(parents=True, exist_ok=True)
data = json.loads(p.read_text()) if p.exists() else {}
llm = data.setdefault("llm", {})
llm["endpoint"] = endpoint
llm["api_key"] = key
p.write_text(json.dumps(data, indent=2) + "\n")
print(f"OK  llm.endpoint = {endpoint}")
print(f"OK  llm.api_key  = {key[:4]}…{key[-4:]} ({len(key)} chars)")
PY
echo "Test: curl -sf -H \"Authorization: Bearer $KEY\" \"$ENDPOINT/v1/models\" | head -c 120"
curl -sf --max-time 5 -H "Authorization: Bearer $KEY" "$ENDPOINT/v1/models" | head -c 200 || echo "(request failed — check key)"
echo
echo "Next: openmono agent   (after Homebrew + finish-setup-macos.sh)"
