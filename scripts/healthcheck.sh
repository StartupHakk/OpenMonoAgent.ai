#!/usr/bin/env bash
set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

echo "OpenMono.ai Health Check"
echo "========================"

# Backend-aware endpoints: Strata serves host-native :8080/v1 (Linux default),
# the legacy llama.cpp path serves :7474. Marker lives in docker/.env.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SCRIPT_DIR/../docker/.env"
BACKEND="$(grep '^INFERENCE_BACKEND=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- | tr -d '[:space:]' || true)"
if [[ "$BACKEND" == "strata" ]] || [[ -z "$BACKEND" && -f "$HOME/.openmono/strata.env" ]]; then
    BACKEND="strata"
    PORT="$(grep '^STRATA_PORT=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- | tr -d '[:space:]' || true)"
    PORT="${PORT:-8080}"
    LABEL="strata"
else
    BACKEND="llama"
    PORT="$(grep '^LLAMA_PORT=' "$ENV_FILE" 2>/dev/null | cut -d= -f2- | tr -d '[:space:]' || true)"
    PORT="${PORT:-7474}"
    LABEL="llama-server"
fi

# Check inference backend
echo -n "$LABEL (localhost:$PORT): "
if curl -sf "http://localhost:$PORT/health" &>/dev/null; then
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
if [[ "$BACKEND" == "llama" ]]; then
    PROPS=$(curl -sf "http://localhost:$PORT/props" 2>/dev/null)
    if [ -n "$PROPS" ]; then
        # Prefer model_alias (set by --alias flag), fall back to model_path basename
        MODEL_NAME=$(echo "$PROPS" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('model_alias') or d.get('model_path','').split('/')[-1].replace('.gguf','') or '')" 2>/dev/null)
        if [ -n "$MODEL_NAME" ]; then
            echo -e "${GREEN}${MODEL_NAME}${NC}"
        else
            echo -e "${GREEN}Loaded${NC} (model name not in /props response)"
        fi
    else
        MODELS=""
    fi
fi
if [[ "$BACKEND" == "strata" ]] || [[ -z "${MODEL_NAME:-}" && -z "${PROPS:-}" ]]; then
    # OpenAI-compatible /v1/models endpoint (both backends serve it)
    MODELS=$(curl -sf "http://localhost:$PORT/v1/models" 2>/dev/null || true)
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
