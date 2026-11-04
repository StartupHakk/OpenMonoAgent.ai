#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Starts a roster desktop: the view-only noVNC password from the mounted
# secret, the base image's desktop session, and the MCP server on $MCP_PORT.
set -euo pipefail
if [[ -r "${VNC_PASSWORD_FILE:-/run/secrets/vnc-password}" ]]; then
  VNC_VIEW_ONLY_PASSWORD="$(tr -d '\n' < "${VNC_PASSWORD_FILE:-/run/secrets/vnc-password}")"
  export VNC_VIEW_ONLY_PASSWORD
fi
# The base image's own start script brings up Xfce, the VNC server and noVNC.
if [[ -x /usr/local/bin/start-desktop ]]; then
  /usr/local/bin/start-desktop &
elif [[ -x /dockerstartup/vnc_startup.sh ]]; then
  /dockerstartup/vnc_startup.sh &
fi
# shellcheck disable=SC2086
exec ${CUA_MCP_COMMAND} --port "${MCP_PORT:-8000}"
