#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# Setup graphify for OpenMono.ai
#
# Installs graphify, builds the knowledge graph from the current working
# directory (or a path you pass), and verifies the MCP server responds.
# ──────────────────────────────────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[38;2;163;255;102m'
NC='\033[0m'

info()  { echo -e "${BLUE}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }

TARGET_DIR="${1:-$(pwd)}"

info "Setting up graphify..."

# Check prerequisites
if ! command -v python3 &>/dev/null; then
    err "Python 3 is required. Install it first."
    exit 1
fi

# Install graphify
if command -v graphify &>/dev/null; then
    ok "graphify already installed"
else
    info "Installing graphify..."
    pip3 install --user graphifyy 2>/dev/null && ok "graphify installed" || {
        warn "pip install failed, trying with --break-system-packages..."
        pip3 install --user --break-system-packages graphifyy 2>/dev/null && ok "Installed" || {
            err "Failed to install graphify. Try: pip3 install graphifyy"
            exit 1
        }
    }
fi

# Verify the target directory exists
if [ ! -d "$TARGET_DIR" ]; then
    err "Directory not found: $TARGET_DIR"
    exit 1
fi

info "Building knowledge graph from $TARGET_DIR..."
info "This may take a while for large codebases."

GRAPHIFY_CMD="graphify"
command -v graphify &>/dev/null || GRAPHIFY_CMD="$HOME/.local/bin/graphify"

# graphify update <path> builds/refreshes the knowledge graph
if (cd "$TARGET_DIR" && "$GRAPHIFY_CMD" update .); then
    ok "Knowledge graph built — output at $TARGET_DIR/graphify-out/graph.json"
else
    err "graphify build failed (exit code $?). Check output above."
    exit 1
fi

GRAPH_JSON="$TARGET_DIR/graphify-out/graph.json"
if [ ! -f "$GRAPH_JSON" ]; then
    err "graph.json not found at $GRAPH_JSON — build may have failed"
    exit 1
fi

# Spot-check: run a query to confirm the graph is readable
info "Verifying graph is queryable..."
if (cd "$TARGET_DIR" && "$GRAPHIFY_CMD" query "main" --budget 50) &>/dev/null; then
    ok "Graph is queryable"
else
    warn "Could not verify graph query (may still work)"
fi

echo ""
ok "Setup complete!"
info "Run 'openmono agent' and ask the agent to use graphify:"
info "  graphify query \"how does authentication work?\""
info "  graphify path \"UserService\" \"TokenValidator\""
info "  graphify explain \"SessionManager\""
