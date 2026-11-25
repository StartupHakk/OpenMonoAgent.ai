#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai 7940HS profile: health and speed check for the native server.
#
# Checks: /health, a request without a key is refused (401), a request with
# the key works, decode tok/s on a fixed 600-token prompt, free RAM idle and
# at its lowest during the request, GPU memory in use, and the APU temperature.
#
# Usage:
#   oma-check.sh [--restart] [--tokens N] [--port P]
#
# The key is read from OMA_KEY_FILE (default /etc/openmono-7940hs/api-keys),
# with sudo when the file is not readable by the caller. It is passed to curl
# through a 0600 temp header file, so it never appears in argv or output.
# Exit codes: 0 all checks passed, 1 a check failed.
# ──────────────────────────────────────────────────────────────────────────────
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONF="${OMA_CONF:-/etc/openmono-7940hs/oma.conf}"
if [[ -r "$CONF" ]]; then
    # shellcheck source=/dev/null
    source "$CONF"
else
    # shellcheck source=profile.conf
    source "$SCRIPT_DIR/profile.conf"
fi

RESTART=0; TOKENS=600; PORT="${OMA_PORT:-7474}"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --restart) RESTART=1; shift ;;
        --tokens)  TOKENS="$2"; shift 2 ;;
        --port)    PORT="$2"; shift 2 ;;
        -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

FAIL=0
pass() { printf '  PASS  %s\n' "$*"; }
fail() { printf '  FAIL  %s\n' "$*"; FAIL=1; }
note() { printf '  ....  %s\n' "$*"; }
avail_mib() { awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo; }

TMP="$(mktemp -d)"; chmod 700 "$TMP"
trap 'rm -rf "$TMP"' EXIT
BASE="http://127.0.0.1:${PORT}"
URL="$BASE/v1/chat/completions"

if [[ "$RESTART" == 1 ]]; then
    note "restarting ${OMA_UNIT:-openmono-7940hs-llama.service}"
    sudo systemctl restart "${OMA_UNIT:-openmono-7940hs-llama.service}" || fail "restart failed"
fi

for _ in $(seq 1 600); do
    curl -sf "$BASE/health" >/dev/null 2>&1 && break
    sleep 1
done
if curl -sf "$BASE/health" >/dev/null 2>&1; then pass "health on port $PORT"; else fail "no /health on port $PORT"; exit 1; fi

# Header file with the key (first non-comment line of the key file).
KEYF="${OMA_KEY_FILE:-/etc/openmono-7940hs/api-keys}"
if [[ -r "$KEYF" ]]; then
    key="$(grep -v '^#' "$KEYF" | head -1)"
else
    key="$(sudo grep -v '^#' "$KEYF" 2>/dev/null | head -1)"
fi
[[ -n "$key" ]] || { fail "could not read a key from $KEYF"; exit 1; }
( umask 077; printf 'Authorization: Bearer %s\n' "$key" > "$TMP/auth.h" )
unset key

code="$(curl -s -o /dev/null -w '%{http_code}' "$URL" -H 'Content-Type: application/json' \
    -d '{"messages":[{"role":"user","content":"hi"}],"max_tokens":5}')"
if [[ "$code" == "401" ]]; then pass "no key -> HTTP 401"; else fail "no key -> HTTP $code (expected 401)"; fi

sleep 3
idle="$(avail_mib)"
( for _ in $(seq 1 300); do avail_mib; sleep 1; done ) > "$TMP/mem.txt" &
MW=$!

body=$(printf '{"messages":[{"role":"user","content":"Explain how a hash map works, with collision handling, load factor and resizing, in about 400 words."}],"max_tokens":%s,"temperature":0}' "$TOKENS")
code="$(curl -s -m 600 -o "$TMP/resp.json" -w '%{http_code}' -H @"$TMP/auth.h" \
    -H 'Content-Type: application/json' "$URL" -d "$body")"
kill "$MW" 2>/dev/null; wait "$MW" 2>/dev/null
gpu_used=0
for dev in /sys/class/drm/card[0-9]*/device; do
    [[ -r "$dev/mem_info_gtt_used" ]] || continue
    gpu_used=$(( ($(cat "$dev/mem_info_vram_used") + $(cat "$dev/mem_info_gtt_used")) / 1048576 )); break
done

if [[ "$code" == "200" ]]; then pass "with key -> HTTP 200"; else fail "with key -> HTTP $code"; fi
if [[ "$code" == "200" ]] && command -v python3 >/dev/null 2>&1; then
    python3 - "$TMP/resp.json" <<'PY' || FAIL=1
import json, sys
d = json.load(open(sys.argv[1]))
t = d.get("timings", {})
c = d["choices"][0]
txt = c["message"].get("content") or ""
tps = t.get("predicted_per_second", 0.0)
print("  ....  model %s, finish %s, %d tokens, %d chars" % (d.get("model"), c.get("finish_reason"), t.get("predicted_n", 0), len(txt)))
print("  %s  decode %.1f tok/s, prompt %.1f tok/s" % ("PASS" if tps > 0 and txt else "FAIL", tps, t.get("prompt_per_second", 0.0)))
sys.exit(0 if tps > 0 and txt else 1)
PY
fi

min="$(sort -n "$TMP/mem.txt" 2>/dev/null | head -1)"
note "MemAvailable idle ${idle} MiB, lowest during request ${min:-?} MiB"
if (( ${min:-0} > 0 && ${min:-0} < 2048 )); then fail "MemAvailable fell under 2 GiB"; fi
if (( gpu_used >= ${OMA_GPU_GUARD_MIN_MIB:-8000} )); then pass "GPU memory in use ${gpu_used} MiB"; else fail "GPU memory in use only ${gpu_used} MiB (CPU fallback?)"; fi
for h in /sys/class/hwmon/hwmon*; do
    [[ "$(cat "$h/name" 2>/dev/null)" == "k10temp" ]] || continue
    t="$(cat "$h/temp1_input" 2>/dev/null || echo 0)"
    note "APU Tctl $(( t / 1000 )) C"
done

if [[ "$FAIL" == 0 ]]; then echo "  RESULT: all checks passed"; else echo "  RESULT: some checks failed"; fi
exit "$FAIL"
