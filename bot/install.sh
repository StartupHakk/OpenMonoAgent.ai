#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Installs the oma-bot roster for the current user. No sudo.
#   ./install.sh [--prefix DIR] [--systemd]
# Layout: PREFIX/bin (oma-bot CLI and service), PREFIX/supervisor,
# PREFIX/hooks (mounted read only into bots), PREFIX/mcp-shim when built.
# The roster home (~/.omabot) is created by `oma-bot init`, never overwritten.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
prefix="${HOME}/.local/share/oma-bot"
systemd=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefix) prefix="$2"; shift 2 ;;
    --systemd) systemd=1; shift ;;
    -h|--help) sed -n '3,8p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null 2>&1 || { echo "dotnet 10 SDK is needed (https://dot.net). Install it for your user and run again." >&2; exit 1; }
if ! command -v docker >/dev/null 2>&1; then
  echo "Note: docker is not on PATH. The roster installs, but bots cannot start until Docker works for this user." >&2
fi

echo "Building into ${prefix}"
mkdir -p "${prefix}/bin" "${prefix}/supervisor" "${prefix}/hooks"
dotnet publish "${here}/src/Omabot/Omabot.csproj" -c Release -o "${prefix}/bin" --nologo -v quiet
dotnet publish "${here}/src/Omabot.Supervisor/Omabot.Supervisor.csproj" -c Release -o "${prefix}/supervisor" --nologo -v quiet
install -m 0755 "${here}"/hooks/*.sh "${prefix}/hooks/"
if [[ -f "${here}/mcp-shim/Omabot.McpShim.csproj" ]] && grep -q "McpShim" "${here}/mcp-shim/Omabot.McpShim.csproj"; then
  mkdir -p "${prefix}/mcp-shim"
  dotnet publish "${here}/mcp-shim/Omabot.McpShim.csproj" -c Release -o "${prefix}/mcp-shim" --nologo -v quiet || echo "MCP shim build skipped." >&2
fi

mkdir -p "${HOME}/.local/bin"
cat > "${HOME}/.local/bin/oma-bot" <<SH
#!/bin/bash
export OMABOT_APP="${prefix}"
exec dotnet "${prefix}/bin/oma-bot.dll" "\$@"
SH
chmod 0755 "${HOME}/.local/bin/oma-bot"
cat > "${HOME}/.local/bin/oma-bot-supervisor" <<SH
#!/bin/bash
exec dotnet "${prefix}/supervisor/Omabot.Supervisor.dll" "\$@"
SH
chmod 0755 "${HOME}/.local/bin/oma-bot-supervisor"

if [[ "${systemd}" == 1 ]]; then
  unit_dir="${HOME}/.config/systemd/user"
  mkdir -p "${unit_dir}"
  cat > "${unit_dir}/oma-bot-supervisor.service" <<UNIT
[Unit]
Description=oma-bot supervisor (only process that talks to Docker)

[Service]
ExecStart=/usr/bin/env dotnet ${prefix}/supervisor/Omabot.Supervisor.dll
Restart=on-failure
UMask=0077

[Install]
WantedBy=default.target
UNIT
  cat > "${unit_dir}/oma-bot.service" <<UNIT
[Unit]
Description=oma-bot roster service (gateway and health loop)
After=oma-bot-supervisor.service

[Service]
Environment=OMABOT_APP=${prefix}
ExecStart=/usr/bin/env dotnet ${prefix}/bin/oma-bot.dll serve
Restart=on-failure
UMask=0077

[Install]
WantedBy=default.target
UNIT
  echo "Wrote user units. Start them with: systemctl --user enable --now oma-bot-supervisor oma-bot"
fi

echo "Installed. Next steps:"
echo "  oma-bot init            # creates ~/.omabot and the default team file"
echo "  edit ~/.omabot/roster.json (gateway.upstream = your FRP relay endpoint)"
echo "  oma-bot team apply ~/.omabot/team/team.yaml"
echo "  oma-bot doctor"
