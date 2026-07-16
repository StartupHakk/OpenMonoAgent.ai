#!/usr/bin/env bash
# Install to /usr/local/bin/openmono (or anywhere on PATH).
# This script owns all Docker flags — users never need to touch docker run directly.
set -euo pipefail

IMAGE="${OPENMONO_IMAGE:-openmono-agent:latest}"
WORKSPACE="${OPENMONO_WORKSPACE:-$(pwd)}"
CONFIG_DIR="${HOME}/.openmono"

# Ensure config dir and subdirs exist so the mount doesn't create them as
# root inside the container (which causes UnauthorizedAccessException).
mkdir -p "${CONFIG_DIR}" "${CONFIG_DIR}/sessions" "${CONFIG_DIR}/memory" "${CONFIG_DIR}/artifacts"

# Base flags always needed
DOCKER_ARGS=(
  --rm
  --interactive
  --tty
  -v "${WORKSPACE}:/workspace"
  -v "${CONFIG_DIR}:/home/agent/.openmono"
  -e "HOME=/home/agent"
  -e "OPENMONO_IN_CONTAINER=1"
)

# Docker socket: lets the agent spawn child containers for tool-specific runtimes.
# Skip if the socket doesn't exist (CI, rootless Docker, etc.)
if [[ -S /var/run/docker.sock ]]; then
  DOCKER_ARGS+=(-v /var/run/docker.sock:/var/run/docker.sock)
fi

# Git auth passthrough: surface the host's git identity + credentials read-only
# so the agent can commit/push to the user's repo. SSH agent forwarding keeps the
# private key on the host. Opt out with OPENMONO_NO_GIT_AUTH=1.
if [[ "${OPENMONO_NO_GIT_AUTH:-0}" != "1" ]]; then
  [[ -f "${HOME}/.gitconfig" ]] && DOCKER_ARGS+=(-v "${HOME}/.gitconfig:/home/agent/.gitconfig:ro")
  [[ -d "${HOME}/.ssh" ]] && DOCKER_ARGS+=(-v "${HOME}/.ssh:/home/agent/.ssh:ro")
  [[ -f "${HOME}/.git-credentials" ]] && DOCKER_ARGS+=(-v "${HOME}/.git-credentials:/home/agent/.git-credentials:ro")
  # macOS: only Docker Desktop's magic socket bridges the agent into the VM.
  # A raw $SSH_AUTH_SOCK bind only works on Linux; elsewhere fall back to the
  # mounted ~/.ssh key files.
  if [[ "$(uname)" == "Darwin" && -d "/Applications/Docker.app" ]]; then
    DOCKER_ARGS+=(-v "/run/host-services/ssh-auth.sock:/ssh-agent" -e "SSH_AUTH_SOCK=/ssh-agent")
  elif [[ "$(uname)" != "Darwin" && -n "${SSH_AUTH_SOCK:-}" && -S "${SSH_AUTH_SOCK}" ]]; then
    DOCKER_ARGS+=(-v "${SSH_AUTH_SOCK}:/ssh-agent" -e "SSH_AUTH_SOCK=/ssh-agent")
  fi
  # Batch-mode ssh + no HTTPS prompt: fail fast instead of hanging on a
  # credential/passphrase prompt the agent can't answer.
  DOCKER_ARGS+=(-e "GIT_SSH_COMMAND=ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes")
  DOCKER_ARGS+=(-e "GIT_TERMINAL_PROMPT=0")
  DOCKER_ARGS+=(-e "GIT_CONFIG_COUNT=1" -e "GIT_CONFIG_KEY_0=safe.directory" -e "GIT_CONFIG_VALUE_0=/workspace")
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
