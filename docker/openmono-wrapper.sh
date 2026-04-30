#!/usr/bin/env bash
# Install to /usr/local/bin/openmono (or anywhere on PATH).
# This script owns all Docker flags — users never need to touch docker run directly.
set -euo pipefail

IMAGE="${OPENMONO_IMAGE:-openmono-agent:latest}"
WORKSPACE="${OPENMONO_WORKSPACE:-$(pwd)}"
CONFIG_DIR="${HOME}/.openmono"

# Ensure config dir exists so the mount doesn't create it as root inside the container
mkdir -p "${CONFIG_DIR}"

# Base flags always needed
DOCKER_ARGS=(
  --rm
  --interactive
  --tty
  -v "${WORKSPACE}:/workspace"
  -v "${CONFIG_DIR}:/root/.openmono"
  -e "OPENMONO_IN_CONTAINER=1"
)

# Docker socket: lets the agent spawn child containers for tool-specific runtimes.
# Skip if the socket doesn't exist (CI, rootless Docker, etc.)
if [[ -S /var/run/docker.sock ]]; then
  DOCKER_ARGS+=(-v /var/run/docker.sock:/var/run/docker.sock)
fi

# LLM endpoint: forward host-local inference server into the container.
# host-gateway resolves to the host's LAN IP on Linux; host.docker.internal on Mac.
if [[ "$(uname)" == "Darwin" ]]; then
  DOCKER_ARGS+=(--add-host "host.docker.internal:host-gateway")
else
  DOCKER_ARGS+=(--add-host "host.docker.internal:host-gateway")
fi

# Forward any OPENMONO_* env vars set in the shell
while IFS= read -r var; do
  DOCKER_ARGS+=(-e "${var}")
done < <(env | grep '^OPENMONO_' || true)

exec docker run "${DOCKER_ARGS[@]}" "${IMAGE}" "$@"
