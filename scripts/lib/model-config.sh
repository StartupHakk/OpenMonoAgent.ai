#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — shared model-config resolver.
#
# settings.json (inference.*) is the single source of truth for model config.
# This file resolves each model knob from settings.json, falling back to the
# value in docker/.env so pre-unification installs (model config only in .env)
# keep working. Readers only — they never write. Writers live in
# scripts/lib/inference-settings.sh (om_persist_*).
#
# Sourced by scripts/apply_settings.sh (and install.sh). All symbols are
# MC_-prefixed to avoid clobbering the host's shell.
# ──────────────────────────────────────────────────────────────────────────────

MC_SETTINGS_FILE="${OPENMONO_SETTINGS_FILE:-$HOME/.openmono/settings.json}"
MC_REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../" && pwd)"
MC_ENV_FILE="${OPENMONO_DOCKER_ENV:-$MC_REPO_DIR/docker/.env}"

# ── low-level reads (jq for settings, sed for .env) ──────────────────────────
_mc_settings_get() {
    # $1 = jq filter; prints the value or nothing.
    [[ -f "$MC_SETTINGS_FILE" ]] || return 0
    jq -r "$1 // empty" "$MC_SETTINGS_FILE" 2>/dev/null || true
}

_mc_env_get() {
    # $1 = key; prints the value (quotes stripped) or nothing.
    [[ -f "$MC_ENV_FILE" ]] || return 0
    local val
    val="$(sed -n "s/^${1}=//p" "$MC_ENV_FILE" 2>/dev/null | head -1 || true)"
    val="${val%\"}"; val="${val#\"}"
    printf '%s' "$val"
}

# ── model knobs (settings.json first, docker/.env fallback) ──────────────────

# Model filename inside models/. The llama server runs --model /models/<name>.
mc_model_name() {
    local v
    v="$(_mc_settings_get '.inference.model_name')"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
    printf '%s' "$(_mc_env_get MODEL_NAME)"
}

# Model alias (llama --alias). Falls back to the filename with the .gguf
# suffix stripped (matching install.sh's MODEL_ALIAS="${MODEL_NAME%.gguf}").
mc_model_alias() {
    local v
    v="$(_mc_settings_get '.inference.model_alias')"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
    v="$(mc_model_name)"
    v="${v%.gguf}"
    printf '%s' "$v"
}

# Vision projector (mmproj) filename; empty when vision is off.
mc_mmproj() {
    local v
    v="$(_mc_settings_get '.inference.mmproj')"
    [[ -n "$v" ]] && { printf '%s' "$v"; return 0; }
    printf '%s' "$(_mc_env_get MODEL_MMPROJ)"
}

# Effective KV-cache context (tokens). Must be a positive integer.
mc_ctx_size() {
    local v
    v="$(_mc_settings_get '.inference.ctx_size')"
    if [[ "$v" =~ ^[0-9]+$ ]] && [ "$v" -gt 0 ]; then printf '%s' "$v"; return 0; fi
    v="$(_mc_env_get CTX_SIZE)"
    if [[ "$v" =~ ^[0-9]+$ ]] && [ "$v" -gt 0 ]; then printf '%s' "$v"; return 0; fi
    return 1
}

# GPU tier: 0=CPU, 8/12/16/24. Not in .env, so settings.json or default 0.
mc_gpu_tier() {
    local v
    v="$(_mc_settings_get '.inference.gpu_tier')"
    if [[ "$v" =~ ^[0-9]+$ ]]; then printf '%s' "$v"; return 0; fi
    printf '%s' 0
}

# Model accuracy: full | lower. Not in .env, so settings.json or default full.
mc_model_accuracy() {
    local v
    v="$(_mc_settings_get '.inference.model_accuracy')"
    case "$v" in
        full|lower) printf '%s' "$v"; return 0 ;;
    esac
    printf '%s' full
}

# Print a one-line summary of the resolved model config (for --check / upgrade).
mc_model_summary() {
    local name ctx tier acc
    name="$(mc_model_name)"
    ctx="$(mc_ctx_size || echo '?')"
    tier="$(mc_gpu_tier)"
    acc="$(mc_model_accuracy)"
    case "$tier" in
        0)  tier_label="CPU" ;;
        8)  tier_label="8GB" ;;
        12) tier_label="12GB" ;;
        16) tier_label="16GB" ;;
        24) tier_label="24GB+" ;;
        *)  tier_label="tier $tier" ;;
    esac
    printf 'model=%s ctx=%s gpu=%s accuracy=%s' \
        "${name:-none}" "$ctx" "$tier_label" "$acc"
}
