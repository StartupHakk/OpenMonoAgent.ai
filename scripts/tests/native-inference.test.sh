#!/usr/bin/env bash
# Checks for the macOS Metal inference helpers. No llama-server required.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=../lib/env-file.sh
source "$ROOT/scripts/lib/env-file.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }

bash -n "$ROOT/openmono" || fail "openmono syntax"
bash -n "$ROOT/scripts/setup-tunnel-tailscale.sh" || fail "tailscale setup syntax"
bash -n "$ROOT/scripts/prepare-dualbox-macos.sh" || fail "prepare syntax"
bash -n "$ROOT/scripts/lib/tailscale-cli.sh" || fail "tailscale cli syntax"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
env_file="$work/docker.env"
printf 'FOO=1\nLLAMA_API_KEY=old\nBAR=2\n' > "$env_file"
upsert_env_key "$env_file" LLAMA_API_KEY "new-key"
grep -qx 'LLAMA_API_KEY=new-key' "$env_file" || fail "key not rotated"
grep -qx 'FOO=1' "$env_file" || fail "dropped unrelated key"
grep -qx 'BAR=2' "$env_file" || fail "dropped trailing key"
[[ ! -e /tmp/env.tmp ]] || fail "wrote shared /tmp/env.tmp"
mode="$(stat -f '%OLp' "$env_file" 2>/dev/null || stat -c '%a' "$env_file")"
[[ "$mode" == "600" ]] || fail "env mode $mode"

drop_env_keys "$env_file" '^(MODEL_MMPROJ|OPENMONO_VISION_ENABLED)='
printf 'MODEL_MMPROJ=%s\n' "mmproj.gguf" >> "$env_file"
grep -qx 'LLAMA_API_KEY=new-key' "$env_file" || fail "drop removed api key"
drop_env_keys "$env_file" '^(MODEL_MMPROJ|OPENMONO_VISION_ENABLED)='
if grep -q '^MODEL_MMPROJ=' "$env_file"; then
    fail "mmproj key survived drop"
fi

printf 'ok\n'
