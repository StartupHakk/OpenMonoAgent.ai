#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Removes the installed roster program files. Keeps ~/.omabot (bots, memory,
# audit, secrets) unless you pass --purge and confirm by typing the word purge.
#   ./uninstall.sh [--prefix DIR] [--purge]
set -euo pipefail
prefix="${HOME}/.local/share/oma-bot"
purge=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefix) prefix="$2"; shift 2 ;;
    --purge) purge=1; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if command -v systemctl >/dev/null 2>&1; then
  systemctl --user disable --now oma-bot.service oma-bot-supervisor.service >/dev/null 2>&1 || true
fi
rm -f "${HOME}/.config/systemd/user/oma-bot.service" "${HOME}/.config/systemd/user/oma-bot-supervisor.service" \
  "${HOME}/.config/systemd/user/oma-bot@.service" "${HOME}/.config/systemd/user/oma-bot-supervisor@.service"
rm -f "${HOME}/.local/bin/oma-bot" "${HOME}/.local/bin/oma-bot-supervisor" "${HOME}/.local/bin/oma-bot-node"
case "${prefix}" in
  ""|"/"|"${HOME}") echo "Refusing to remove ${prefix}" >&2; exit 1 ;;
esac
rm -rf "${prefix}"
echo "Program files removed. Bot containers are left as they are; stop them first with: oma-bot down"

if [[ "${purge}" == 1 ]]; then
  home="${OMABOT_HOME:-${HOME}/.omabot}"
  read -r -p "Type purge to delete ${home} (all bots, memory, audit and secrets): " answer
  if [[ "${answer}" == "purge" ]]; then
    rm -rf "${home}"
    echo "Deleted ${home}."
  else
    echo "Kept ${home}."
  fi
fi
