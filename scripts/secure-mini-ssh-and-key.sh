#!/usr/bin/env bash
# Open interactive SSH to Mini to install this Mac's pubkey + show LLAMA_API_KEY.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MINI_HOST="${MINI_HOST:-shawns-mac-mini-3.taild31afe.ts.net}"
MINI_USER="${MINI_USER:-abc}"
PUBKEY_FILE="${HOME}/.ssh/id_ed25519.pub"

if [[ ! -f "$PUBKEY_FILE" ]]; then
  echo "Missing $PUBKEY_FILE — generate with: ssh-keygen -t ed25519 -C openmono-imac"
  exit 1
fi

PUBKEY="$(tr -d '\n' < "$PUBKEY_FILE")"
pbcopy < "$PUBKEY_FILE" 2>/dev/null || true
FP="$(ssh-keygen -lf "$PUBKEY_FILE" | awk '{print $2}')"

# Build a one-liner remote script (no nested heredoc in AppleScript).
REMOTE=$(cat <<EOF
mkdir -p ~/.ssh && chmod 700 ~/.ssh; touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys; grep -qxF '$PUBKEY' ~/.ssh/authorized_keys || echo '$PUBKEY' >> ~/.ssh/authorized_keys; echo OK: pubkey installed; echo; echo LLAMA_API_KEY candidates:; grep -h '^LLAMA_API_KEY=' ~/openmono.ai/docker/.env ~/Downloads/OpenMonoAgent.ai-macOS-inference-support/docker/.env ~/Downloads/OpenMonoAgent.ai/docker/.env 2>/dev/null || true; echo; echo On iMac: cd $REPO_DIR && ./scripts/apply-mini-endpoint.sh KEY; exec bash -l
EOF
)

# Escape for embedding inside double-quoted AppleScript string
REMOTE_ESC="${REMOTE//\\/\\\\}"
REMOTE_ESC="${REMOTE_ESC//\"/\\\"}"

osascript <<APPLESCRIPT
tell application "Terminal"
  activate
  do script "ssh -t ${MINI_USER}@${MINI_HOST} \"${REMOTE_ESC}\""
end tell
APPLESCRIPT

echo "Opened Terminal → ssh ${MINI_USER}@${MINI_HOST}"
echo "Enter the Mini login password when prompted."
echo "Then on this Mac: ./scripts/apply-mini-endpoint.sh <KEY>"
echo "Pubkey fingerprint: $FP (also on clipboard)"
