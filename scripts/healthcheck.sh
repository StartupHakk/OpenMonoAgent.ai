#!/usr/bin/env bash
set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

echo "OpenMono.ai Health Check"
echo "========================"

# Check llama-server
echo -n "llama-server (localhost:7474): "
if curl -sf http://localhost:7474/health &>/dev/null; then
    echo -e "${GREEN}HEALTHY${NC}"
else
    echo -e "${RED}UNREACHABLE${NC}"
fi

# Check Docker containers
echo ""
echo "Docker containers:"
docker compose -f "$(dirname "$0")/../docker/docker-compose.yml" ps 2>/dev/null || echo "  (not running)"

# Check model actually loaded in the running server
echo ""
echo -n "Model loaded: "
PROPS=$(curl -sf http://localhost:7474/props 2>/dev/null)
if [ -n "$PROPS" ]; then
    # Prefer model_alias (set by --alias flag), fall back to model_path basename
    MODEL_NAME=$(echo "$PROPS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('model_alias') or d.get('model_path','').split('/')[-1].replace('.gguf','') or '')" 2>/dev/null)
    if [ -n "$MODEL_NAME" ]; then
        echo -e "${GREEN}${MODEL_NAME}${NC}"
    else
        echo -e "${GREEN}Loaded${NC} (model name not in /props response)"
    fi
else
    # Fallback: OpenAI-compatible /v1/models endpoint (local only)
    MODELS=$(curl -sf http://localhost:7474/v1/models 2>/dev/null)
    if [ -n "$MODELS" ]; then
        MODEL_NAME=$(echo "$MODELS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['data'][0]['id'] if d.get('data') else '')" 2>/dev/null)
        if [ -n "$MODEL_NAME" ]; then
            echo -e "${GREEN}${MODEL_NAME}${NC} (via /v1/models)"
        else
            echo -e "${GREEN}Loaded${NC} (model name not in /v1/models response)"
        fi
    else
        echo -e "${RED}Server not reachable — cannot confirm model${NC}"
    fi
fi

# Check code-review-graph
echo -n "code-review-graph: "
if command -v code-review-graph &>/dev/null; then
    VERSION=$(code-review-graph --version 2>/dev/null || echo "unknown")
    echo -e "${GREEN}Installed${NC} ($VERSION)"

    # Check if graph database exists
    GRAPH_DB="$HOME/.openmono/graph-db"
    echo -n "  Graph database: "
    if [ -d "$GRAPH_DB" ] && [ -n "$(ls -A "$GRAPH_DB" 2>/dev/null)" ]; then
        echo -e "${GREEN}Built${NC}"
    else
        echo -e "${RED}Not built${NC} — run: ./scripts/setup-graph.sh"
    fi
else
    echo -e "${RED}Not installed${NC} — run: pip3 install code-review-graph"
fi
