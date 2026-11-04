#!/usr/bin/env bash
# Phase 0 task 6: start one OMA agent container by hand with its own name,
# data dir and loopback port, without editing any OMA file.
# Run only on a scratch machine or with Spencer's go-ahead.
# SPDX-License-Identifier: AGPL-3.0-or-later
set -euo pipefail

NAME="${1:-scratch}"
PORT="${2:-7599}"
BASE="${OMABOT_SCRATCH:-$HOME/.omabot-scratch}/$NAME"
IMAGE="${OMA_AGENT_IMAGE:-openmono-agent:latest}"

mkdir -p "$BASE/data" "$BASE/workspace"
chmod 700 "$BASE" "$BASE/data"

docker run -d \
  --name "oma-bot-$NAME" \
  --label oma.roster=1 --label "oma.roster.bot=$NAME" \
  --cap-drop ALL --security-opt no-new-privileges --pids-limit 512 \
  --memory 1536m --cpus 1.0 \
  -p "127.0.0.1:$PORT:7475" \
  -v "$BASE/workspace:/workspace" \
  -v "$BASE/data:/home/agent/.openmono" \
  -v /dev/null:/home/agent/.openmono/host-bridge.json \
  --tmpfs /home/agent/.openmono/host-bridge/logs \
  --tmpfs /home/agent/.openmono/logs \
  -e HOME=/home/agent \
  -e OPENMONO_WORKSPACE=/workspace \
  -e OPENMONO_ACP_ENABLED=1 \
  -e ACP_PORT=7475 \
  "$IMAGE" --acp-only

echo "Waiting for discovery on 127.0.0.1:$PORT"
for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:$PORT/api/v1/discovery" >/dev/null 2>&1; then
    echo "ready"; exit 0
  fi
  sleep 1
done
echo "discovery did not answer in 60 seconds" >&2
exit 1
