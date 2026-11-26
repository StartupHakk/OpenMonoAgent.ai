#!/bin/bash
# Setup SSH key auth from this Mac to Mac Mini 3 (shawns-mac-mini-3).
set -euo pipefail

TARGET_USER="${1:-abc}"
TARGET_HOST="${2:-100.84.136.111}"
KEY="$HOME/.ssh/id_ed25519"
PUB="${KEY}.pub"
COMMENT="${SSH_KEY_COMMENT:-openmono-imac}"

mkdir -p "$HOME/.ssh"
chmod 700 "$HOME/.ssh"

if [[ ! -f "$KEY" ]]; then
  echo "Generating ed25519 key (empty passphrase for agent convenience)..."
  ssh-keygen -t ed25519 -N "" -f "$KEY" -C "$COMMENT"
  chmod 600 "$KEY"
  chmod 644 "$PUB"
  echo "Created $KEY (you can add a passphrase later with: ssh-keygen -p -f $KEY)"
else
  echo "Using existing key: $KEY"
fi

echo ""
echo "=== Public key (safe to share) ==="
cat "$PUB"
echo "================================="
echo ""

if command -v ssh-copy-id >/dev/null 2>&1; then
  echo "Copying public key to ${TARGET_USER}@${TARGET_HOST} (enter Mini password once)..."
  ssh-copy-id -i "$PUB" "${TARGET_USER}@${TARGET_HOST}"
else
  echo "ssh-copy-id not found. On the Mini, append this line to ~/.ssh/authorized_keys"
  echo "(mkdir -p ~/.ssh && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys):"
  echo ""
  cat "$PUB"
  echo ""
  read -r -p "Press Enter after authorized_keys is updated on the Mini..."
fi

echo ""
echo "Testing key auth (BatchMode)..."
ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new \
  "${TARGET_USER}@${TARGET_HOST}" 'hostname; uname -m'
echo "OK: passwordless SSH works for agents."
