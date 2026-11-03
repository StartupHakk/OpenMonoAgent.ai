#!/usr/bin/env bash
# Finish OpenMono setup after Xcode CLT + Homebrew/Docker are installed.
# Run: ./scripts/finish-setup-macos.sh
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export REPO_DIR
export OPENMONO_ROLE=agent

echo "==> Running full agent setup..."
"$REPO_DIR/openmono" setup --agent --verbose

echo ""
echo "==> Setup complete. Next:"
echo "    openmono config set llm.endpoint http://<inference-host>:7474"
echo "    openmono config set llm.api_key  <your-api-key>"
echo "    cd your-project && openmono agent"
