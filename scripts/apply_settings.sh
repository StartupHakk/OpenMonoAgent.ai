#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — apply ~/.openmono/settings.json to the active inference backend.
#
# settings.json is the desired state. This script materializes only the shared
# runtime knobs into the files the backend actually reads:
#
#   inference.port           -> INFERENCE_PORT (docker/.env, strata.env), Caddy
#   inference.parallel_slots -> INFERENCE_PARALLEL, Strata config
#   llm.api_key              -> LLAMA_API_KEY, Strata config api_key
#                               (generated when missing; never rotated)
#   inference.ctx_size       -> CTX_SIZE (llama/native), Strata --max-context
#   inference.vision_enabled -> OPENMONO_VISION_ENABLED
#   llm.endpoint             -> local endpoint (remote/relay endpoints untouched)
#
# Everything else in docker/.env, strata.env, strata-*.json and run-*.sh is
# install-time state and is deliberately preserved.
#
# Usage:
#   apply_settings.sh --check [--quiet]   # report drift; exit 2 when out of sync
#   apply_settings.sh --apply [--quiet]   # write the derived files
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

SETTINGS_FILE="${OPENMONO_SETTINGS_FILE:-$HOME/.openmono/settings.json}"
STATE_DIR="${OPENMONO_STATE_DIR:-$HOME/.openmono}"
DOCKER_ENV="${OPENMONO_DOCKER_ENV:-$REPO_DIR/docker/.env}"
STRATA_ENV="$STATE_DIR/strata.env"
DEFAULT_PORT=7474
DEFAULT_PARALLEL=1

# Shared model-config resolver (settings.json → docker/.env fallback).
# MC_* symbols are read-only; the model knobs are persisted by
# scripts/lib/inference-settings.sh (om_persist_*).
source "$SCRIPT_DIR/lib/model-config.sh"

MODE="check"
QUIET=0

for arg in "$@"; do
    case "$arg" in
        --apply)   MODE="apply" ;;
        --check)   MODE="check" ;;
        --quiet|-q) QUIET=1 ;;
        --help|-h)
            awk 'NR>4 { if ($0 ~ /^# ─/) exit; sub(/^# ?/, ""); print }' "$0"
            exit 0
            ;;
        *)
            echo "apply_settings.sh: unknown argument: $arg" >&2
            exit 1
            ;;
    esac
done

info() { [[ "$QUIET" == "1" ]] || echo -e "${BLUE:-}[settings]${NC:-} $*"; }
warn() { echo -e "${YELLOW:-}[settings]${NC:-} $*" >&2; }
err()  { echo -e "${RED:-}[settings]${NC:-} $*" >&2; }
BLUE=$'\033[38;2;163;255;102m'
YELLOW=$'\033[1;33m'
RED=$'\033[0;31m'
NC=$'\033[0m'

_need_jq() {
    command -v jq >/dev/null 2>&1 || { err "jq is required."; exit 1; }
}

_env_get() {
    local key="$1" file="$2" val
    [[ -f "$file" ]] || return 0
    val="$(sed -n "s/^${key}=//p" "$file" 2>/dev/null | head -1 || true)"
    # strata.env quotes values; docker/.env does not. Normalize for comparison.
    val="${val%\"}"; val="${val#\"}"
    printf '%s' "$val"
}

_env_set() {
    local key="$1" val="$2" file="$3" tmp
    mkdir -p "$(dirname "$file")"
    touch "$file"
    tmp="$(mktemp "${file}.tmp.XXXXXX")"
    grep -v "^${key}=" "$file" > "$tmp" 2>/dev/null || true
    printf '%s=%s\n' "$key" "$val" >> "$tmp"
    mv "$tmp" "$file"
    chmod 0600 "$file" 2>/dev/null || true
}

_env_unset() {
    local key="$1" file="$2" tmp
    [[ -f "$file" ]] || return 0
    tmp="$(mktemp "${file}.tmp.XXXXXX")"
    grep -v "^${key}=" "$file" > "$tmp" 2>/dev/null || true
    mv "$tmp" "$file"
    chmod 0600 "$file" 2>/dev/null || true
}

_settings_get() {
    [[ -f "$SETTINGS_FILE" ]] || return 0
    jq -r "$1 // empty" "$SETTINGS_FILE" 2>/dev/null || true
}

_settings_update() {
    # $1 = jq filter; rest = jq args
    local filter="$1"; shift
    local tmp
    mkdir -p "$(dirname "$SETTINGS_FILE")"
    [[ -f "$SETTINGS_FILE" ]] || echo '{}' > "$SETTINGS_FILE"
    tmp="$(mktemp "${SETTINGS_FILE}.tmp.XXXXXX")"
    if jq "$@" "$filter" "$SETTINGS_FILE" > "$tmp" 2>/dev/null; then
        mv "$tmp" "$SETTINGS_FILE"
        chmod 0600 "$SETTINGS_FILE" 2>/dev/null || true
    else
        rm -f "$tmp"
        warn "Could not update $SETTINGS_FILE"
        return 1
    fi
}

# True (0) when the jq path $1 resolves to a non-empty value. Used to detect
# whether a model knob is already recorded in settings.json before migrating
# from docker/.env.
_settings_has() {
    [[ -f "$SETTINGS_FILE" ]] || return 1
    local v
    v="$(jq -r "$1 // empty" "$SETTINGS_FILE" 2>/dev/null || true)"
    [[ -n "$v" ]]
}

_is_native() {
    [[ "$(uname -s)" == "Darwin" && "$(uname -m)" == "arm64" ]]
}

_backend() {
    if [[ -n "${OPENMONO_FORCE_BACKEND:-}" ]]; then
        printf '%s' "$OPENMONO_FORCE_BACKEND"
        return
    fi
    if _is_native; then
        printf 'native-metal'
        return
    fi
    local b
    b="$(_env_get INFERENCE_BACKEND "$DOCKER_ENV")"
    case "$b" in
        strata|llama) printf '%s' "$b"; return ;;
    esac
    if [[ -f "$STRATA_ENV" ]]; then
        printf 'strata'
    else
        printf 'llama'
    fi
}

# Agent-only boxes mount settings.json too; never invent a local inference
# endpoint or rewrite docker/.env on those.
_is_inference_box() {
    [[ -n "${OPENMONO_FORCE_BACKEND:-}" ]] && return 0
    if _is_native; then
        # A macOS agent-only install has no native model recorded; don't invent
        # a local endpoint or rewrite its docker/.env.
        [[ -n "$(_settings_get '.inference.model_name')" ]] && return 0
        return 1
    fi
    [[ -f "$STRATA_ENV" ]] && return 0
    [[ -n "$(_env_get INFERENCE_BACKEND "$DOCKER_ENV")" ]] && return 0
    [[ -n "$(_env_get MODEL_NAME "$DOCKER_ENV")" ]] && return 0
    [[ -n "$(_env_get INFERENCE_PORT "$DOCKER_ENV")" ]] && return 0
    [[ -n "$(_env_get LLAMA_PORT "$DOCKER_ENV")" ]] && return 0
    [[ -n "$(_env_get STRATA_PORT "$DOCKER_ENV")" ]] && return 0
    [[ -n "$(_settings_get '.inference.port')" ]] && return 0
    [[ -n "$(_settings_get '.inference.parallel_slots')" ]] && return 0
    return 1
}

_applied_port() {
    local p=""
    # INFERENCE_PORT is the applied key. The backend-specific names are read
    # only to migrate installs made before the unification.
    p="$(_env_get INFERENCE_PORT "$DOCKER_ENV")"
    if [[ -z "$p" ]]; then
        case "$(_backend)" in
            strata)
                p="$(_env_get INFERENCE_PORT "$STRATA_ENV")"
                [[ -n "$p" ]] || p="$(_env_get STRATA_PORT "$STRATA_ENV")"
                [[ -n "$p" ]] || p="$(_env_get STRATA_PORT "$DOCKER_ENV")"
                ;;
            native-metal)
                p="$(_settings_get '.inference.port')"
                [[ -n "$p" ]] || p="$(_env_get LLAMA_PORT "$DOCKER_ENV")"
                ;;
            *)
                p="$(_env_get LLAMA_PORT "$DOCKER_ENV")"
                ;;
        esac
    fi
    printf '%s' "${p:-$DEFAULT_PORT}"
}

_applied_parallel() {
    local p=""
    case "$(_backend)" in
        strata)
            p="$(_env_get INFERENCE_PARALLEL "$DOCKER_ENV")"
            [[ -n "$p" ]] || p="$(_env_get INFERENCE_PARALLEL "$STRATA_ENV")"
            [[ -n "$p" ]] || p="$(_env_get STRATA_PARALLEL "$STRATA_ENV")"
            [[ -n "$p" ]] || p="$(_env_get STRATA_PARALLEL "$DOCKER_ENV")"
            ;;
        native-metal)
            p="$(_settings_get '.inference.parallel_slots')"
            ;;
        *)
            p="$(_env_get INFERENCE_PARALLEL "$DOCKER_ENV")"
            [[ -n "$p" ]] || p="$(_env_get LLAMA_PARALLEL "$DOCKER_ENV")"
            ;;
    esac
    printf '%s' "${p:-$DEFAULT_PARALLEL}"
}

_desired_port() {
    local p
    p="$(_settings_get '.inference.port')"
    if [[ "$p" =~ ^[0-9]+$ ]] && (( p >= 1 && p <= 65535 )); then
        printf '%s' "$p"
        return
    fi
    p="$(_applied_port)"
    # One-time migration from the old Strata default; custom ports survive.
    if [[ "$p" == "8080" ]]; then
        p="$DEFAULT_PORT"
    fi
    printf '%s' "${p:-$DEFAULT_PORT}"
}

_desired_parallel() {
    local n
    n="$(_settings_get '.inference.parallel_slots')"
    if [[ "$n" =~ ^[0-9]+$ ]] && (( n >= 1 )); then
        printf '%s' "$n"
        return
    fi
    n="$(_applied_parallel)"
    printf '%s' "${n:-$DEFAULT_PARALLEL}"
}

_desired_api_key() {
    local key
    key="$(_settings_get '.llm.api_key')"
    [[ -n "$key" ]] || key="$(_env_get LLAMA_API_KEY "$DOCKER_ENV")"
    printf '%s' "$key"
}

_new_api_key() {
    local key=""
    command -v openssl >/dev/null 2>&1 && key="$(openssl rand -hex 24 2>/dev/null || true)"
    [[ -n "$key" ]] || key="$(head -c 24 /dev/urandom 2>/dev/null | od -An -tx1 | tr -d ' \n' || true)"
    printf '%s' "$key"
}

# Every local inference backend takes a bearer key: llama-server runs with
# --api-key, and Strata requires one and refuses to start without it on a
# non-loopback bind. An empty key is an unauthenticated model endpoint on the
# LAN, so always make one. llm.api_key is the desired state; the docker/.env
# and Strata config writes below turn it into applied state.
_ensure_api_key() {
    local key="$1"
    [[ -z "$key" ]] || { printf '%s' "$key"; return 0; }
    key="$(_new_api_key)"
    if [[ -z "$key" ]]; then
        warn "could not generate an inference API key. Set it with:"
        warn "  openmono config set llm.api_key <key>"
        printf '%s' ""
        return 0
    fi
    _settings_update '.llm = (.llm // {}) | .llm.api_key = $k' --arg k "$key" || true
    printf '%s' "$key"
}

_desired_ctx() {
    local ctx
    ctx="$(_settings_get '.inference.ctx_size')"
    if [[ "$ctx" =~ ^[0-9]+$ ]] && (( ctx > 0 )); then
        printf '%s' "$ctx"
    fi
}

_desired_vision() {
    local v
    v="$(_settings_get '.inference.vision_enabled')"
    case "$v" in
        true|false) printf '%s' "$v" ;;
    esac
}

_desired_endpoint() {
    local port="$1" ep
    ep="$(_settings_get '.llm.endpoint')"
    case "$ep" in
        ""|http://localhost|http://localhost:*|http://localhost/*|\
        http://127.0.0.1|http://127.0.0.1:*|http://127.0.0.1/*|\
        http://host.docker.internal|http://host.docker.internal:*|http://host.docker.internal/*)
            # ConfigLoader strips a trailing /v1; keep the base URL canonical.
            printf 'http://127.0.0.1:%s' "$port"
            ;;
        *)
            printf '%s' "$ep"
            ;;
    esac
}

_caddy_upstream() {
    local port="$1"
    case "$(_backend)" in
        strata|native-metal) printf 'host.docker.internal:%s' "$port" ;;
        *)                   printf 'llama-server:7474' ;;
    esac
}

_find_run_script() {
    local dir="$1"
    ls -t "$dir"/run-*.sh 2>/dev/null | head -1 || true
}

_apply_strata() {
    local port="$1" parallel="$2" key="$3" ctx="$4" run_script cfg_path
    run_script="$(_find_run_script "$HOME/strata")"
    if [[ -z "$run_script" && -f "$STRATA_ENV" ]]; then
        # shellcheck source=/dev/null
        local sdir
        sdir="$(bash -c 'source "$1" 2>/dev/null; printf "%s" "${STRATA_DIR:-}"' _ "$STRATA_ENV")"
        [[ -n "$sdir" ]] && run_script="$(_find_run_script "$sdir")"
    fi
    if [[ -z "$run_script" ]]; then
        warn "No run-*.sh found — Strata config/port will be applied on the next install/start."
        return 0
    fi
    cfg_path="$(sed -n -E 's/.*--config\"?[[:space:]]*\"([^\"]*)\".*/\1/p' "$run_script" 2>/dev/null | head -1)"
    [[ -n "$cfg_path" ]] || cfg_path="$(sed -n -E 's/.*--config[[:space:]]+([^[:space:]]+).*/\1/p' "$run_script" 2>/dev/null | head -1)"
    [[ -n "$cfg_path" && -f "$cfg_path" ]] || cfg_path=""

    # Rewrite the port in the generated run script. Upstream quotes the flag
    # and value ("--port" "8080"), but hand-edited scripts may not.
    local old
    old="$(sed -n -E 's/.*"--port"[[:space:]]*"?([0-9]+)"?.*/\1/p' "$run_script" 2>/dev/null | head -1)"
    [[ -n "$old" ]] || old="$(sed -n -E 's/.*--port[[:space:]]+"?([0-9]+)"?.*/\1/p' "$run_script" 2>/dev/null | head -1)"
    if [[ -n "$old" && "$old" != "$port" ]]; then
        local tmp
        tmp="$(mktemp "${run_script}.tmp.XXXXXX")"
        sed -E \
            -e "s/(\"--port\"[[:space:]]*\")[0-9]+(\")/\1${port}\2/" \
            -e "s/(--port[[:space:]]*\")[0-9]+(\")/\1${port}\2/" \
            -e "s/(--port[[:space:]]+)[0-9]+/\1${port}/" \
            "$run_script" > "$tmp"
        mv "$tmp" "$run_script"
        chmod +x "$run_script" 2>/dev/null || true
        info "run script port: $old -> $port"
    fi

    if [[ -n "$cfg_path" ]] && command -v python3 >/dev/null 2>&1; then
        python3 - "$cfg_path" "$port" "$parallel" "$key" "$ctx" "${STRATA_HOST:-0.0.0.0}" <<'PY'
import json, sys
path, port, parallel, key, ctx, host = sys.argv[1:7]
try:
    with open(path) as f:
        cfg = json.load(f)
except Exception:
    sys.exit(0)
changed = []
if host and cfg.get("host") != host:
    cfg["host"] = host; changed.append("host")
if key and cfg.get("api_key") != key:
    cfg["api_key"] = key; changed.append("api_key")
p = int(port)
if cfg.get("port") != p:
    cfg["port"] = p; changed.append("port")
n = int(parallel)
if cfg.get("parallel") != n:
    cfg["parallel"] = n; changed.append("parallel")
if ctx:
    args = cfg.get("args") or []
    for flag in ("--max-context", "--ctx-size"):
        if flag in args:
            i = args.index(flag)
            if i + 1 < len(args) and args[i + 1] != str(ctx):
                args[i + 1] = str(ctx); changed.append("context")
            break
if changed:
    with open(path, "w") as f:
        json.dump(cfg, f, indent=1)
    print("[settings] strata config updated (" + ", ".join(changed) + "): " + path)
PY
    elif [[ -n "$cfg_path" ]]; then
        warn "python3 not found — Strata config was not updated."
    fi
}

_check() {
    local backend port parallel key ctx vision ep upstream drift=0 vision_env=""
    backend="$(_backend)"
    port="$(_desired_port)"
    parallel="$(_desired_parallel)"
    key="$(_desired_api_key)"
    ctx="$(_desired_ctx)"
    vision="$(_desired_vision)"
    ep="$(_desired_endpoint "$port")"
    upstream="$(_caddy_upstream "$port")"
    # Model config (settings.json is canonical; mc_* falls back to .env so
    # pre-unification installs report no drift against their applied state).
    model_name="$(mc_model_name)"
    model_alias="$(mc_model_alias)"
    proj="$(mc_mmproj)"
    tier="$(mc_gpu_tier)"
    acc="$(mc_model_accuracy)"
    case "$vision" in
        true)  vision_env=1 ;;
        false) vision_env=0 ;;
    esac

    info "desired: backend=$backend port=$port slots=$parallel"
    if [[ -n "$ctx" ]]; then info "desired: ctx=$ctx"; fi
    if [[ -n "$vision" ]]; then info "desired: vision=$vision"; fi
    info "desired: $(mc_model_summary)"

    _check_env() {
        local file="$1" key="$2" want="$3" got
        [[ -n "$want" ]] || return 0
        got="$(_env_get "$key" "$file")"
        if [[ "$got" != "$want" ]]; then
            echo "  drift: $key=$got (want $want)  [$file]" >&2
            drift=1
        fi
    }

    # Backend-specific legacy keys are migration-only; apply removes them.
    _check_absent() {
        local file="$1" key="$2" got
        got="$(_env_get "$key" "$file")"
        if [[ -n "$got" ]]; then
            echo "  stale: $key=$got (superseded by the shared INFERENCE_* key)  [$file]" >&2
            drift=1
        fi
    }

    _check_env "$DOCKER_ENV" INFERENCE_PORT "$port"
    _check_absent "$DOCKER_ENV" LLAMA_PORT
    _check_absent "$DOCKER_ENV" STRATA_PORT
    _check_env "$DOCKER_ENV" INFERENCE_PARALLEL "$parallel"
    _check_absent "$DOCKER_ENV" LLAMA_PARALLEL
    _check_absent "$DOCKER_ENV" STRATA_PARALLEL
    _check_env "$DOCKER_ENV" LLAMA_API_KEY "$key"
    if [[ -z "$key" ]]; then
        echo "  drift: llm.api_key is empty, but the inference server needs a bearer key" >&2
        drift=1
    fi
    _check_env "$DOCKER_ENV" LLAMA_UPSTREAM "$upstream"
    _check_env "$DOCKER_ENV" CTX_SIZE "$ctx"
    # Model config drift (settings.json is canonical; mc_* falls back to .env).
    if [[ -n "$model_name" ]]; then
        _check_env "$DOCKER_ENV" MODEL_NAME "$model_name"
    fi
    if [[ -n "$model_alias" ]]; then
        _check_env "$DOCKER_ENV" MODEL_ALIAS "$model_alias"
    fi
    _check_env "$DOCKER_ENV" MODEL_MMPROJ "$proj"
    _check_env "$DOCKER_ENV" OPENMONO_VISION_ENABLED "$vision_env"

    case "$backend" in
        strata)
            _check_env "$STRATA_ENV" INFERENCE_PORT "$port"
            _check_absent "$STRATA_ENV" STRATA_PORT
            _check_env "$STRATA_ENV" INFERENCE_PARALLEL "$parallel"
            _check_absent "$STRATA_ENV" STRATA_PARALLEL
            ;;
    esac

    # The local endpoint is part of the applied state for single-box installs.
    local cur_ep
    cur_ep="$(_settings_get '.llm.endpoint')"
    if [[ -n "$cur_ep" && "$cur_ep" != "$ep" ]]; then
        case "$cur_ep" in
            http://localhost|http://localhost:*|http://localhost/*|\
            http://127.0.0.1|http://127.0.0.1:*|http://127.0.0.1/*|\
            http://host.docker.internal|http://host.docker.internal:*|http://host.docker.internal/*)
                echo "  drift: llm.endpoint=$cur_ep (want $ep)" >&2
                drift=1
                ;;
        esac
    fi

    if [[ "$drift" == "1" ]]; then
        return 2
    fi
    info "applied state matches settings."
    return 0
}

_apply() {
    local backend port parallel key ctx vision ep upstream
    backend="$(_backend)"
    port="$(_desired_port)"
    parallel="$(_desired_parallel)"
    key="$(_desired_api_key)"
    ctx="$(_desired_ctx)"
    vision="$(_desired_vision)"
    ep="$(_desired_endpoint "$port")"
    upstream="$(_caddy_upstream "$port")"
    # Model config (settings.json is canonical; mc_* falls back to .env).
    model_name="$(mc_model_name)"
    model_alias="$(mc_model_alias)"
    proj="$(mc_mmproj)"
    tier="$(mc_gpu_tier)"
    acc="$(mc_model_accuracy)"

    _need_jq
    mkdir -p "$STATE_DIR"
    [[ -f "$SETTINGS_FILE" ]] || echo '{}' > "$SETTINGS_FILE"

    # Every backend serves a bearer-protected API; make a key if the install never did.
    local wanted_key="$key"
    key="$(_ensure_api_key "$key")"
    if [[ -n "$key" && -z "$wanted_key" ]]; then
        info "generated an inference API key: llm.api_key"
    fi

    # Pin the resolved defaults into settings.json so it is the single source.
    _settings_update '.inference = (.inference // {}) | .inference.port = $p | .inference.parallel_slots = $n' \
        --argjson p "$port" --argjson n "$parallel" || true
    if [[ -n "$ep" ]]; then
        _settings_update '.llm = (.llm // {}) | .llm.endpoint = $e' --arg e "$ep" || true
    fi

    # Model-config migration: pre-unification installs recorded model config
    # only in docker/.env. Backfill it into settings.json (the new source of
    # truth) exactly once — only when the key is absent, so a user's explicit
    # `openmono config set inference.model_name …` is never clobbered.
    if ! _settings_has '.inference.model_name' && [[ -n "$model_name" ]]; then
        _settings_update '.inference = (.inference // {}) | .inference.model_name = $n' \
            --arg n "$model_name" || true
    fi
    if ! _settings_has '.inference.model_alias' && [[ -n "$model_alias" ]]; then
        _settings_update '.inference = (.inference // {}) | .inference.model_alias = $n' \
            --arg n "$model_alias" || true
    fi
    if ! _settings_has '.inference.mmproj' && [[ -n "$proj" ]]; then
        _settings_update '.inference = (.inference // {}) | .inference.mmproj = $n' \
            --arg n "$proj" || true
    fi
    # ctx_size uses mc_ctx_size (falls back to .env) rather than $ctx, which
    # reads settings.json only — otherwise a pre-unification install whose ctx
    # lives only in .env would never be backfilled.
    local ctx_source
    ctx_source="$(mc_ctx_size || true)"
    if ! _settings_has '.inference.ctx_size' && [[ -n "$ctx_source" ]]; then
        _settings_update '.inference = (.inference // {}) | .inference.ctx_size = $n' \
            --argjson n "$ctx_source" || true
    fi
    if ! _settings_has '.inference.gpu_tier' && [[ -n "$tier" ]]; then
        _settings_update '.inference = (.inference // {}) | .inference.gpu_tier = $n' \
            --argjson n "$tier" || true
    fi
    if ! _settings_has '.inference.model_accuracy' && [[ -n "$acc" ]]; then
        _settings_update '.inference = (.inference // {}) | .inference.model_accuracy = $n' \
            --arg n "$acc" || true
    fi

    _env_set INFERENCE_PORT "$port" "$DOCKER_ENV"
    # Migration cleanup: these were separate backend ports before the
    # unification; INFERENCE_PORT is now the only applied key.
    _env_unset LLAMA_PORT "$DOCKER_ENV"
    _env_unset STRATA_PORT "$DOCKER_ENV"
    _env_set INFERENCE_PARALLEL "$parallel" "$DOCKER_ENV"
    _env_unset LLAMA_PARALLEL "$DOCKER_ENV"
    _env_unset STRATA_PARALLEL "$DOCKER_ENV"
    _env_set LLAMA_UPSTREAM "$upstream" "$DOCKER_ENV"
    if [[ -n "$key" ]]; then
        _env_set LLAMA_API_KEY "$key" "$DOCKER_ENV"
    fi
    if [[ -n "$ctx" ]]; then
        _env_set CTX_SIZE "$ctx" "$DOCKER_ENV"
    fi
    # Model config into docker/.env (applied state). The llama override template
    # resolves MODEL_NAME / MODEL_ALIAS / MODEL_MMPROJ / CTX_SIZE from this file
    # at compose time, so this is what makes the server pick up a model swap or
    # ctx change without a re-install. mmproj is always written (empty = vision
    # off) to match install.sh, which records an explicit empty value.
    if [[ -n "$model_name" ]]; then
        _env_set MODEL_NAME "$model_name" "$DOCKER_ENV"
    fi
    if [[ -n "$model_alias" ]]; then
        _env_set MODEL_ALIAS "$model_alias" "$DOCKER_ENV"
    fi
    _env_set MODEL_MMPROJ "$proj" "$DOCKER_ENV"
    case "$vision" in
        true)  _env_set OPENMONO_VISION_ENABLED 1 "$DOCKER_ENV" ;;
        false) _env_set OPENMONO_VISION_ENABLED 0 "$DOCKER_ENV" ;;
    esac
    case "$backend" in
        strata) _env_set INFERENCE_BACKEND strata "$DOCKER_ENV" ;;
        llama)  _env_set INFERENCE_BACKEND llama "$DOCKER_ENV" ;;
    esac

    if [[ "$backend" == "strata" ]]; then
        if [[ -f "$STRATA_ENV" ]]; then
            _env_set INFERENCE_PORT "$port" "$STRATA_ENV"
            _env_unset STRATA_PORT "$STRATA_ENV"
            _env_set INFERENCE_PARALLEL "$parallel" "$STRATA_ENV"
            _env_unset STRATA_PARALLEL "$STRATA_ENV"
            if [[ -n "$ctx" ]]; then
                _env_set STRATA_CONTEXT "$ctx" "$STRATA_ENV"
            fi
        else
            warn "Strata state file not found: $STRATA_ENV (run scripts/strata.sh install)"
        fi
        _apply_strata "$port" "$parallel" "$key" "$ctx"
    fi

    info "applied: backend=$backend port=$port slots=$parallel upstream=$upstream"
    [[ -n "$ep" ]] && info "local endpoint: $ep"
}

if [[ ! -f "$SETTINGS_FILE" ]]; then
    info "no settings file at $SETTINGS_FILE — nothing to apply."
    exit 0
fi

if ! _is_inference_box; then
    info "no local inference backend on this box — nothing to apply."
    exit 0
fi

case "$MODE" in
    check) _check ;;
    apply) _apply ;;
esac
