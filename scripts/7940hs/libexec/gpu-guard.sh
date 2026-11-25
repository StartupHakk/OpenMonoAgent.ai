#!/usr/bin/env bash
# OpenMono.ai 7940HS profile: GPU guard (ExecStartPost of the llama service).
# Waits for /health, then fails the unit unless VRAM + GTT in use reaches
# OMA_GPU_GUARD_MIN_MIB, which means the model really loaded onto the iGPU.
set -uo pipefail

CONF="${OMA_CONF:-/etc/openmono-7940hs/oma.conf}"
# shellcheck source=/dev/null
source "$CONF"
MIN="${OMA_GPU_GUARD_MIN_MIB:-8000}"
WAIT="${OMA_GPU_GUARD_WAIT_S:-840}"

for _ in $(seq 1 "$WAIT"); do
    curl -sf "http://127.0.0.1:${OMA_PORT}/health" >/dev/null 2>&1 && break
    sleep 1
done
if ! curl -sf "http://127.0.0.1:${OMA_PORT}/health" >/dev/null 2>&1; then
    echo "gpu-guard: server not healthy after ${WAIT}s" >&2
    exit 1
fi

used=0
for dev in /sys/class/drm/card[0-9]*/device; do
    [[ -r "$dev/mem_info_gtt_used" ]] || continue
    vram="$(cat "$dev/mem_info_vram_used" 2>/dev/null || echo 0)"
    gtt="$(cat "$dev/mem_info_gtt_used" 2>/dev/null || echo 0)"
    used=$(( (vram + gtt) / 1048576 ))
    break
done
if (( used < MIN )); then
    echo "gpu-guard: FAIL: only ${used} MiB VRAM+GTT in use (need ${MIN}); llama-server is likely on the CPU" >&2
    exit 1
fi
echo "gpu-guard: OK: ${used} MiB VRAM+GTT in use"
exit 0
