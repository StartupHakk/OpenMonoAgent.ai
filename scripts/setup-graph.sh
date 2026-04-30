#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# Setup code-review-graph for OpenMono.ai
#
# Installs code-review-graph, builds the knowledge graph from the current
# working directory (or ref/ if populated), and verifies the MCP server works.
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
REF_DIR="$ROOT_DIR/ref"
GRAPH_DB_DIR="${HOME}/.openmono/graph-db"

info "Setting up code-review-graph..."

# Check prerequisites
if ! command -v python3 &>/dev/null; then
    err "Python 3 is required. Install it first."
    exit 1
fi

# Install code-review-graph
if command -v code-review-graph &>/dev/null; then
    ok "code-review-graph already installed"
else
    info "Installing code-review-graph..."
    pip3 install --user code-review-graph 2>/dev/null && ok "code-review-graph installed" || {
        warn "pip install failed, trying with --break-system-packages..."
        pip3 install --user --break-system-packages code-review-graph 2>/dev/null && ok "Installed" || {
            err "Failed to install code-review-graph. Try: pip3 install code-review-graph"
            exit 1
        }
    }
fi

# Determine what to index
# If argument is passed, always use it. Otherwise default to ref/
REPO_DIR=""
if [ -n "${1:-}" ] && [ -d "$1" ]; then
    REPO_DIR="$1"
    info "Building graph from $REPO_DIR..."
elif [ -d "$REF_DIR" ] && [ -n "$(ls -A "$REF_DIR" 2>/dev/null)" ]; then
    REPO_DIR="$REF_DIR"
    info "Building graph from ref/ directory..."
else
    warn "No source to index."
    info "Usage:"
    info "  $0                  # Indexes ref/ directory (add code there first)"
    info "  $0 /path/to/project # Indexes a specific project"
    info ""
    info "Example:"
    info "  cp -r ~/my-project $REF_DIR/my-project"
    info "  $0"
    exit 0
fi

# Build the graph
mkdir -p "$GRAPH_DB_DIR"

GRAPH_CMD="code-review-graph"
command -v code-review-graph &>/dev/null || GRAPH_CMD="$HOME/.local/bin/code-review-graph"

if [ -x "$(command -v "$GRAPH_CMD" 2>/dev/null)" ] || [ -x "$GRAPH_CMD" ]; then
    # code-review-graph 2.x removed --output; graph is stored in ~/.code-review-graph/ by default
    "$GRAPH_CMD" build --repo "$REPO_DIR" && \
        ok "Code graph built successfully" || \
        warn "Graph build had warnings — may still be usable"

    # Ensure database is properly initialized and migrated
    "$GRAPH_CMD" postprocess
else
    err "code-review-graph not found in PATH. Add ~/.local/bin to your PATH."
    exit 1
fi

# Verify MCP server works
info "Verifying MCP server..."
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0.1.0"}}}' \
    | timeout 5 "$GRAPH_CMD" serve 2>/dev/null | head -1 | python3 -c "
import json, sys
try:
    r = json.loads(sys.stdin.readline())
    if 'result' in r:
        print('\033[0;32m[OK]\033[0m MCP server responds correctly')
    else:
        print('\033[1;33m[WARN]\033[0m Unexpected response:', r)
except:
    print('\033[1;33m[WARN]\033[0m Could not verify MCP server (may still work)')
" 2>/dev/null || warn "Could not verify MCP server (may still work)"

echo ""
ok "Setup complete!"
info "OpenMono.ai will auto-detect code-review-graph at startup."
info "Run 'openmono agent' and the 22 code graph tools will be available via MCP."
