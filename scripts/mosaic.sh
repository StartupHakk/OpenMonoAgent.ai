#!/usr/bin/env bash
set -euo pipefail

# OpenMono Mosaic engine helper (CPU scaffold). Not the installer default.
# Does not download multi-GB weights. See docs/ENGINE-DESIGN.md.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENGINE_DIR="$REPO_DIR/engine"

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; BLUE=$'\033[38;2;163;255;102m'; NC=$'\033[0m'
info() { echo -e "${BLUE}[mosaic]${NC} $*"; }
ok()   { echo -e "${GREEN}[mosaic]${NC} $*"; }
err()  { echo -e "${RED}[mosaic]${NC} $*" >&2; }

if [[ ! -d "$ENGINE_DIR/openmono_engine" ]]; then
    err "engine/ is missing at $ENGINE_DIR"
    exit 1
fi

cmd_check() {
    info "Python: $(command -v python3 || echo missing)"
    PYTHONPATH="$ENGINE_DIR" python3 - <<'PY'
from openmono_engine.adapters.registry import AdapterRegistry
from openmono_engine.hardware import best_fit_24_64
r = AdapterRegistry()
row = best_fit_24_64()
print("families:", ", ".join(r.families()))
print("default:", r.default_family)
print("best 24/64 fit:", row.name, row.quant, f"{row.file_gb} GiB", row.hf_repo)
PY
    ok "Mosaic CPU core imports."
}

cmd_test() {
    info "Running engine unit tests ..."
    PYTHONPATH="$ENGINE_DIR" python3 -m unittest discover -s "$ENGINE_DIR/tests" -v
}

cmd_plan() {
    PYTHONPATH="$ENGINE_DIR" python3 -m openmono_engine.strata_plan "$@"
}

usage() {
    cat <<EOF
OpenMono Mosaic (CPU scaffold)

  mosaic.sh check          import adapters + print 24/64 best fit
  mosaic.sh test           python3 -m unittest discover -s engine/tests
  mosaic.sh plan [--mem-gb N] [--family F] [--model M]

Not started by 'openmono start'. Not a production inference server yet.
EOF
}

case "${1:-help}" in
    check) shift; cmd_check "$@" ;;
    test)  shift; cmd_test "$@" ;;
    plan)  shift; cmd_plan "$@" ;;
    help|--help|-h|*) usage ;;
esac
