#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai Native Inference Commands (macOS Apple Silicon)
#
# Provides implementations of start/stop/restart/logs/status/agent/tunnel
# for native llama-server running via Homebrew with Metal GPU acceleration.
#
# Sources: openmono CLI passes $REPO_DIR and other vars. This script
# should be sourced by openmono and its functions called via native_cmd_*()
# ──────────────────────────────────────────────────────────────────────────────

# Load settings.json and set native-specific variables
native_load_config() {
    local settings="$HOME/.openmono/settings.json"
    if ! command -v jq &>/dev/null; then
        err "jq is required. Install: brew install jq"; return 1
    fi
    if [[ ! -f "$settings" ]]; then
        err "Settings not found: $settings"; err "Re-run: openmono setup"; return 1
    fi
    MODEL_NAME=$(jq -r '.inference.model_name // empty' "$settings" 2>/dev/null)
    MODEL_ALIAS=$(jq -r '.inference.model_alias // empty' "$settings" 2>/dev/null)
    LLAMA_CTX_SIZE=$(jq -r '.inference.ctx_size // 65536' "$settings" 2>/dev/null)
    LLAMA_NATIVE_PORT=$(jq -r '.inference.port // "7474"' "$settings" 2>/dev/null)
    LLAMA_INSTALL_DIR=$(jq -r '.inference.install_dir // empty' "$settings" 2>/dev/null)
    LLAMA_MMPROJ_PATH=$(jq -r '.inference.mmproj_path // empty' "$settings" 2>/dev/null)
    LLAMA_INSTALL_DIR="${LLAMA_INSTALL_DIR:-$HOME/openmono.ai}"
    if [[ -z "$MODEL_NAME" ]]; then
        err "inference config not found in $settings"; err "Re-run: openmono setup"; return 1
    fi
}

# Start native llama-server in the background with Metal GPU.
# Requires native_load_config to have been called first.
native_start_llama() {
    if ! command -v llama-server &>/dev/null; then
        err "llama-server not found. Install: brew install llama.cpp"; return 1
    fi
    local model_file="$LLAMA_INSTALL_DIR/models/$MODEL_NAME"
    if [[ ! -f "$model_file" ]]; then
        err "Model not found: $model_file"; return 1
    fi
    local log_file="$HOME/.openmono/logs/llama-server.log"
    mkdir -p "$(dirname "$log_file")"

    local cpu_threads
    cpu_threads=$(sysctl -n hw.physicalcpu 2>/dev/null || echo 4)

    # Read API key from docker/.env (single source of truth)
    local api_key=""
    local docker_env="${REPO_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}/docker/.env"
    if [[ -f "$docker_env" ]]; then
        api_key=$(grep '^LLAMA_API_KEY=' "$docker_env" 2>/dev/null | cut -d= -f2- | tr -d '[:space:]' || true)
    fi

    info "Starting llama-server (Metal) — model: $MODEL_NAME"
    nohup llama-server \
        --model "$model_file" --alias "${MODEL_ALIAS:-${MODEL_NAME%.gguf}}" \
        --host 0.0.0.0 --port "${LLAMA_NATIVE_PORT:-7474}" \
        --n-gpu-layers 99 \
        --ctx-size "$LLAMA_CTX_SIZE" \
        --threads "$cpu_threads" --batch-size 2048 --ubatch-size 512 \
        --flash-attn on \
        --cache-type-k q8_0 --cache-type-v q8_0 \
        --parallel 1 --jinja --reasoning off --metrics \
        ${api_key:+--api-key "${api_key}"} \
        ${LLAMA_MMPROJ_PATH:+--mmproj "${LLAMA_MMPROJ_PATH}" --image-min-tokens 1024 --image-max-tokens 1280} \
        > "$log_file" 2>&1 &
    local pid=$!
    disown $pid

    # Sanity check: ensure process didn't die immediately
    sleep 0.5
    if ! kill -0 "$pid" 2>/dev/null; then
        err "llama-server failed to start (process exited immediately)"
        err "Check the log for details:"
        err "  tail -20 $log_file"
        return 1
    fi
}

# Stop native llama-server by port with safety check (verify it's really llama-server).
# Args: $1 = port (optional, defaults to LLAMA_PORT)
native_stop_llama() {
    local port="${1:-${LLAMA_NATIVE_PORT:-7474}}"

    # Check if llama-server is actually responding on the port (best indicator that it's really running)
    if ! curl -sf "http://localhost:${port}/health" &>/dev/null; then
        warn "llama-server is not responding on port ${port}"
        return 1
    fi

    # Find llama-server process by name (more reliable than port-based lookup)
    local pid
    pid=$(pgrep -f "llama-server.*--port.*${port}" 2>/dev/null | head -1 || true)

    if [[ -z "$pid" ]]; then
        warn "llama-server process not found (port ${port} may be in use by something else)"
        return 1
    fi

    info "Stopping llama-server (PID $pid) on port ${port}..."
    kill "$pid" 2>/dev/null || true
    sleep 1
    ok "Stopped"
}

# ──────────────────────────────────────────────────────────────────────────────
# Native command implementations (called from openmono)
# ──────────────────────────────────────────────────────────────────────────────

native_cmd_start() {
    native_load_config || return 1
    local port="${LLAMA_NATIVE_PORT:-7474}"

    # Check if llama-server is already healthy (best indicator)
    if curl -sf "http://localhost:${port}/health" &>/dev/null; then
        ok "llama-server already running on port ${port}"
        return 0
    fi

    native_start_llama || return 1

    info "Waiting for llama-server to be healthy (model load: 1-3 min)..."
    for i in $(seq 1 36); do
        if curl -sf "http://localhost:${port}/health" &>/dev/null; then
            ok "llama-server is healthy on port ${port} (Metal GPU)"
            return 0
        fi
        sleep 5
        printf "."
    done
    echo ""
    warn "llama-server did not become healthy within 180s — check: tail -f $HOME/.openmono/logs/llama-server.log"
    return 1
}

native_cmd_stop() {
    native_load_config || return 1
    native_stop_llama "${LLAMA_NATIVE_PORT:-7474}" || return 1
    ok "llama-server stopped"
}

native_cmd_restart() {
    native_load_config || return 1
    local port="${LLAMA_NATIVE_PORT:-7474}"
    native_stop_llama "$port" || true
    sleep 1
    native_start_llama || return 1
    info "Waiting for llama-server to be healthy..."
    for i in $(seq 1 36); do
        if curl -sf "http://localhost:${port}/health" &>/dev/null; then
            ok "llama-server restarted and healthy on port ${port} (Metal GPU)"
            return 0
        fi
        sleep 5
        printf "."
    done
    echo ""
    warn "llama-server did not become healthy within 180s"
    return 1
}

native_cmd_logs() {
    local log_file="$HOME/.openmono/logs/llama-server.log"
    if [[ ! -f "$log_file" ]]; then
        err "Log file not found: $log_file"
        return 1
    fi
    tail -f "$log_file"
}

native_cmd_status() {
    native_load_config || return 1
    local port="${LLAMA_NATIVE_PORT:-7474}"
    echo ""
    echo "╭─ Native Inference (Metal GPU) ────────────────────────────╮"

    # Check if running
    local pid proc_name
    pid=$(lsof -ti ":${port}" 2>/dev/null | head -1 || true)
    if [[ -n "$pid" ]]; then
        proc_name=$(ps -p "$pid" -o comm= 2>/dev/null || true)
        if [[ "$proc_name" == *"llama-server"* ]]; then
            local cpu mem
            cpu=$(ps -p "$pid" -o %cpu= 2>/dev/null || echo "—")
            mem=$(ps -p "$pid" -o %mem= 2>/dev/null || echo "—")
            printf "│ Status          : ${GREEN}Running${NC} (PID $pid)\n"
            printf "│ CPU/Mem         : %s%% / %s%%\n" "$cpu" "$mem"
        else
            printf "│ Status          : ${YELLOW}Port in use${NC} by $proc_name (PID $pid)\n"
        fi
    else
        printf "│ Status          : ${RED}Not running${NC}\n"
    fi

    printf "│ Port            : %s\n" "$port"
    printf "│ Model           : %s\n" "$MODEL_NAME"
    printf "│ Context Size    : %s\n" "$LLAMA_CTX_SIZE"
    if [[ -n "$LLAMA_MMPROJ_PATH" ]]; then
        printf "│ Vision          : ${GREEN}enabled${NC} (%s)\n" "$(basename "$LLAMA_MMPROJ_PATH")"
    else
        printf "│ Vision          : disabled\n"
    fi

    # Health check
    if curl -sf "http://localhost:${port}/health" &>/dev/null; then
        printf "│ Health Endpoint : ${GREEN}✓${NC} http://localhost:${port}/health\n"
    else
        printf "│ Health Endpoint : ${YELLOW}✗${NC} http://localhost:${port}/health (offline)\n"
    fi

    # Apple Silicon info
    local chip_name memory_gb
    chip_name=$(sysctl -n machdep.cpu.brand_string 2>/dev/null || echo "Apple Silicon")
    memory_gb=$(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1024 / 1024 / 1024 ))
    printf "│ Apple Silicon   : %s (%s GB)\n" "$chip_name" "$memory_gb"

    echo "╰────────────────────────────────────────────────────────────╯"
    echo ""
}

# Ensure local llama-server is running on Metal. Must call native_load_config first.
native_ensure_running() {
    local port="${LLAMA_NATIVE_PORT:-7474}"

    if ! curl -sf "http://localhost:${port}/health" &>/dev/null; then
        info "llama-server not running — starting it (Metal, model load: 1–3 min)..."
        native_start_llama || return 1
        info "Waiting for llama-server to be healthy..."
        for i in $(seq 1 36); do
            if curl -sf "http://localhost:${port}/health" &>/dev/null; then
                ok "llama-server is healthy"
                break
            fi
            sleep 5
            printf "."
        done
        echo ""
    fi
}

native_cmd_tunnel_rotate_key() {
    native_load_config || return 1
    local port="${LLAMA_NATIVE_PORT:-7474}"

    # Generate new API key
    local new_key
    new_key="$(openssl rand -hex 24)"

    # Update docker/.env with new API key (single source of truth)
    local docker_env="${REPO_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}/docker/.env"
    if [[ ! -f "$docker_env" ]]; then
        mkdir -p "$(dirname "$docker_env")"
        touch "$docker_env"
    fi
    grep -v '^LLAMA_API_KEY=' "$docker_env" > /tmp/env.tmp 2>/dev/null || true
    echo "LLAMA_API_KEY=$new_key" >> /tmp/env.tmp
    mv /tmp/env.tmp "$docker_env"
    chmod 0600 "$docker_env"
    ok "LLAMA_API_KEY rotated in docker/.env"

    # Stop and restart llama-server with new key
    local pid proc_name
    pid=$(lsof -ti ":${port}" 2>/dev/null | head -1 || true)
    if [[ -n "$pid" ]]; then
        proc_name=$(ps -p "$pid" -o comm= 2>/dev/null || true)
        if [[ "$proc_name" == *"llama-server"* ]]; then
            native_stop_llama "$port" || true
            sleep 1
            info "Restarting native llama-server with new API key..."
            native_start_llama || warn "Restart failed — run manually: openmono start"
            ok "llama-server restarted (Metal)"
        else
            warn "Port ${port} is in use by '${proc_name}' (PID $pid) — not llama-server"
            warn "Manually restart llama-server after verifying port is free: openmono restart"
        fi
    else
        warn "llama-server is not running — start it with: openmono start"
    fi

    # Print config commands for agent box (same as Docker path)
    local relay_cache="$HOME/.openmono/relay.json"
    local endpoint=""
    if [[ -f "$relay_cache" ]]; then
        local frps_addr remote_port
        frps_addr="$(jq -r '.frpsAddress // empty' "$relay_cache" 2>/dev/null)"
        remote_port="$(jq -r '.remotePort // empty' "$relay_cache" 2>/dev/null)"
        [[ -n "$frps_addr" && -n "$remote_port" ]] && \
            endpoint="http://$frps_addr:$remote_port"
    fi

    echo ""
    printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    printf "${BLUE}${BOLD}  API Key Rotated${NC}\n"
    printf "${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 60))"
    echo ""
    echo "  Run the following on the agent box to apply the new key:"
    echo ""
    [[ -n "$endpoint" ]] && \
        printf "    ${BOLD}openmono config set llm.endpoint  %s${NC}\n" "$endpoint"
    printf "    ${BOLD}openmono config set llm.api_key   %s${NC}\n" "$new_key"
    echo ""
}
