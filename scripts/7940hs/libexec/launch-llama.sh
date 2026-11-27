#!/usr/bin/env bash
# OpenMono.ai 7940HS profile: llama-server launcher (ExecStart of
# openmono-7940hs-llama.service). All settings come from the installed config;
# edit /etc/openmono-7940hs/oma.conf, then: systemctl restart openmono-7940hs-llama
# The API key is read by llama-server from the key file; it is never printed.
set -euo pipefail

CONF="${OMA_CONF:-/etc/openmono-7940hs/oma.conf}"
[[ -r "$CONF" ]] || { echo "launch-llama: cannot read $CONF" >&2; exit 1; }
# shellcheck source=/dev/null
source "$CONF"

: "${OMA_LLAMA_BIN:?OMA_LLAMA_BIN not set in $CONF}"
: "${OMA_MODEL_PATH:?OMA_MODEL_PATH not set in $CONF}"
[[ -x "$OMA_LLAMA_BIN" ]] || { echo "launch-llama: $OMA_LLAMA_BIN missing" >&2; exit 1; }
[[ -r "$OMA_MODEL_PATH" ]] || { echo "launch-llama: model $OMA_MODEL_PATH not readable" >&2; exit 1; }
[[ -r "$OMA_KEY_FILE" ]] || { echo "launch-llama: key file $OMA_KEY_FILE not readable by $(id -un)" >&2; exit 1; }

# Pre-flight half of the GPU guard: the render node must be openable, or
# Vulkan quietly falls back to the CPU.
render_ok=0
for n in /dev/dri/renderD*; do
    [[ -r "$n" && -w "$n" ]] && { render_ok=1; break; }
done
if [[ "$render_ok" != 1 ]]; then
    echo "launch-llama: GPU GUARD: $(id -un) cannot open /dev/dri/renderD* (add it to the render group)" >&2
    exit 1
fi

LLAMA_LIB_DIR="$(dirname "$OMA_LLAMA_BIN")"
export LD_LIBRARY_PATH="${LLAMA_LIB_DIR}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# OMA_ENV_EXTRA: space separated NAME=VALUE pairs, e.g. RADV_PERFTEST=... (TUNING)
for kv in ${OMA_ENV_EXTRA:-}; do
    [[ "$kv" == *=* ]] && export "${kv?}"
done

args=(
    -m "$OMA_MODEL_PATH"
    --alias "${OMA_MODEL_ALIAS},model"
    --host "$OMA_HOST" --port "$OMA_PORT"
    -c "$OMA_CTX" --parallel "$OMA_PARALLEL"
    -ngl "$OMA_NGL" -b "$OMA_BATCH" -ub "$OMA_UBATCH"
    -fa "$OMA_FLASH_ATTN" -ctk "$OMA_CACHE_K" -ctv "$OMA_CACHE_V"
    --no-host --load-mode "$OMA_LOAD_MODE"
    --jinja --metrics
    --reasoning "$OMA_REASONING"
    --api-key-file "$OMA_KEY_FILE"
)
[[ -n "${OMA_TEMPLATE_KWARGS:-}" ]] && args+=(--chat-template-kwargs "$OMA_TEMPLATE_KWARGS")
# Host prompt cache cap in MiB (-cram). Keeps RAM flat on long agent sessions.
[[ -n "${OMA_CACHE_RAM:-}" ]] && args+=(-cram "$OMA_CACHE_RAM")
if [[ -n "${OMA_EXTRA_ARGS:-}" ]]; then
    # shellcheck disable=SC2206  # word splitting of free-form extra flags is intended
    extra=( $OMA_EXTRA_ARGS )
    args+=("${extra[@]}")
fi

exec "$OMA_LLAMA_BIN" "${args[@]}"
